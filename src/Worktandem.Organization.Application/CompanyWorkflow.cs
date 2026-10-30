using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Worktandem.Organization.Domain;

namespace Worktandem.Organization.Application;

public sealed class CompanyWorkflow(ICompanyTransactions transactions, IIdentityAuthority authority, TimeProvider clock)
{
    public const string WorkspacePermission = "company.workspace.read";

    public Task<ApplicationView> Submit(ActorRequest actor, Guid operationId, CompanyForm form, string key,
        string correlation, CancellationToken ct) => Authorized(actor, "organization.account", async (state, identity, token) =>
        {
            form = Normalize(form); ValidateKey(key);
            await state.LockOperation(identity.Identity.AccountId, token);
            RequireFresh(identity, actor);
            if (operationId == Guid.Empty || operationId != (identity.Identity.InitialRegistrationOperationId ?? identity.Identity.CompanyCreationOperationId))
                throw Failure("invalid_creation_operation", 409);
            var hash = Hash(new { operationId, form });
            var replay = await Replay<ApplicationView>(state, identity.Identity.AccountId, "submit", Guid.Empty, key, hash, token);
            if (replay is not null) return replay;
            var existing = await state.ByOperation(identity.Identity.AccountId, operationId, token);
            if (existing is not null)
            {
                var first = await state.FirstApplication(existing.Id, token) ?? throw Failure("organization_unavailable", 503);
                if (Form(first) != form) throw Failure("creation_payload_conflict", 409);
                // Durable actor/operation uniqueness survives HTTP receipt expiry and uncertain delivery.
                var resumed = await View(state, existing, token);
                Receipt(state, identity, "submit", Guid.Empty, key, hash, resumed);
                return resumed;
            }
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7();
            var company = new Company(id, form.CompanyName, "company-" + id.ToString("N"), CompanyStatus.Pending,
                null, identity.Identity.AccountId, operationId, now, now, Guid.CreateVersion7());
            state.Add(company);
            await state.Save(token);
            var application = NewApplication(company, form, 1, now);
            state.Add(application);
            await state.Save(token);
            company.Submit(application, now);
            Audit(state, company, application, identity, "application.submitted", null, correlation, now);
            var result = new ApplicationView(company.Id, company.Version, company.Status.ToString(), application.Id,
                application.Version, 1, form, null, now);
            Receipt(state, identity, "submit", Guid.Empty, key, hash, result);
            return result;
        }, ct);

    public Task<ApplicationView> Resubmit(ActorRequest actor, Guid companyId, Guid expectedVersion, CompanyForm form,
        string key, string correlation, CancellationToken ct) => Authorized(actor, "organization.account", async (state, identity, token) =>
        {
            form = Normalize(form); ValidateKey(key);
            var company = await state.LockCompany(companyId, token) ?? throw Failure("not_found", 404);
            RequireFresh(identity, actor);
            Own(company, identity);
            var hash = Hash(new { expectedVersion, form });
            var replay = await Replay<ApplicationView>(state, identity.Identity.AccountId, "resubmit", companyId, key, hash, token);
            if (replay is not null) return replay;
            Version(company, expectedVersion);
            if (company.Status != CompanyStatus.Rejected) throw Failure("resubmission_unavailable", 409);
            var prior = await state.Application(company.CurrentApplicationId!.Value, token) ?? throw Failure("organization_unavailable", 503);
            var now = clock.GetUtcNow(); var previous = company.Version;
            var application = NewApplication(company, form, checked(prior.SubmissionNumber + 1), now);
            state.Add(application);
            await state.Save(token);
            company.Submit(application, now);
            Audit(state, company, application, identity, "application.resubmitted", previous, correlation, now);
            var result = new ApplicationView(company.Id, company.Version, company.Status.ToString(), application.Id,
                application.Version, application.SubmissionNumber, form, null, now);
            Receipt(state, identity, "resubmit", companyId, key, hash, result);
            return result;
        }, ct);

    public Task<ApplicationView> Decide(ActorRequest actor, Guid companyId, Guid applicationId, Guid expectedVersion,
        ReviewCommand command, string key, string correlation, CancellationToken ct) => Authorized(actor, "platform.companies.review", async (state, identity, token) =>
        {
            ValidateKey(key);
            if (!Enum.IsDefined(command.Outcome) || command.InternalNote?.Length > 2000 || command.ApplicantVisibleReason?.Length > 2000 ||
                (command.Outcome == ReviewOutcome.Rejected && string.IsNullOrWhiteSpace(command.ApplicantVisibleReason)))
                throw Failure("invalid_decision", 422);
            var company = await state.LockCompany(companyId, token) ?? throw Failure("not_found", 404);
            // Remote admission precedes this transaction. Reject evidence that aged out while waiting locally.
            RequireFresh(identity, actor);
            if (company.CreatedByAccountId == identity.Identity.AccountId) throw Failure("self_review_denied", 403);
            var hash = Hash(new { applicationId, expectedVersion, command });
            var replay = await Replay<ApplicationView>(state, identity.Identity.AccountId, "decision", companyId, key, hash, token);
            if (replay is not null) return replay;
            Version(company, expectedVersion);
            if (company.Status != CompanyStatus.Pending || company.CurrentApplicationId != applicationId)
                throw Failure("application_not_current", 409);
            var application = await state.Application(applicationId, token) ?? throw Failure("not_found", 404);
            if (application.CompanyId != companyId || application.Version != command.ApplicationVersion)
                throw Failure("application_version_conflict", 412);
            if (application.ApplicantAccountId == identity.Identity.AccountId) throw Failure("self_review_denied", 403);
            if (await state.Decision(applicationId, token) is not null) throw Failure("already_decided", 409);
            var now = clock.GetUtcNow(); var previous = company.Version;
            state.Add(new ReviewDecision(Guid.CreateVersion7(), companyId, applicationId, identity.Identity.AccountId,
                application.Version, command.Outcome, now, identity.EvidenceId.ToString("D"), key,
                command.ApplicantVisibleReason?.Trim(), command.InternalNote?.Trim()));
            company.Decide(command.Outcome, now);
            if (command.Outcome == ReviewOutcome.Approved)
            {
                // Only this activation path creates the initial Owner. No pre-approval membership exists.
                var member = new CompanyMembership(Guid.CreateVersion7(), companyId, application.ApplicantAccountId,
                    MembershipStatus.Active, now, null, now, now, Guid.CreateVersion7());
                var role = new CompanyRole(Guid.CreateVersion7(), companyId, "Owner", "OWNER", true, now, now, Guid.CreateVersion7());
                state.Add(member); state.Add(role);
                state.Add(new RolePermission(companyId, role.Id, WorkspacePermission));
                state.Add(new CompanyRoleAssignment(companyId, member.Id, role.Id, now));
            }
            Audit(state, company, application, identity, "application." + command.Outcome.ToString().ToLowerInvariant(), previous, correlation, now);
            var result = new ApplicationView(company.Id, company.Version, company.Status.ToString(), application.Id,
                application.Version, application.SubmissionNumber, Form(application), command.ApplicantVisibleReason?.Trim(), application.SubmittedAt);
            Receipt(state, identity, "decision", companyId, key, hash, result);
            return result;
        }, ct);

    public Task<CompanyContext> Context(ActorRequest actor, CancellationToken ct) => Authorized(actor, "organization.account", async (state, identity, token) =>
    {
        var applications = new List<ApplicationView>(); var workspaces = new List<WorkspaceView>();
        foreach (var candidate in (await state.OwnCompanies(identity.Identity.AccountId, token)).OrderBy(c => c.Id))
        {
            var company = await state.LockCompany(candidate.Id, token);
            if (company is not null) applications.Add(await View(state, company, token));
        }
        foreach (var member in (await state.Memberships(identity.Identity.AccountId, token)).OrderBy(m => m.CompanyId))
        {
            var company = await state.LockCompany(member.CompanyId, token);
            var current = (await state.Memberships(identity.Identity.AccountId, token)).SingleOrDefault(m => m.Id == member.Id);
            if (company?.Status != CompanyStatus.Approved || current?.Status != MembershipStatus.Active) continue;
            var permissions = await state.Permissions(company.Id, member.Id, token);
            if (permissions.Contains(WorkspacePermission, StringComparer.Ordinal))
                workspaces.Add(new(company.Id, company.Version, company.Name, member.Id,
                    identity.Identity.AccountId, identity.Identity.AccountSessionId, permissions));
        }
        RequireFresh(identity, actor);
        return new CompanyContext(applications, workspaces);
    }, ct);

    public Task<ApplicationView> OwnApplication(ActorRequest actor, Guid operationId, CancellationToken ct) =>
        Authorized(actor, "organization.account", async (state, identity, token) =>
        {
            var company = await state.ByOperation(identity.Identity.AccountId, operationId, token) ?? throw Failure("not_found", 404);
            company = await state.LockCompany(company.Id, token) ?? throw Failure("not_found", 404);
            RequireFresh(identity, actor);
            Own(company, identity);
            return await View(state, company, token);
        }, ct);

    public Task<IReadOnlyList<ReviewView>> Queue(ActorRequest actor, int offset, int limit, CancellationToken ct) =>
        Authorized<IReadOnlyList<ReviewView>>(actor, "platform.companies.read", async (state, identity, token) =>
        {
            if (offset < 0 || offset > 100000 || limit is < 1 or > 100) throw Failure("invalid_page", 422);
            var result = new List<ReviewView>();
            foreach (var company in await state.ReviewQueue(offset, limit, token))
                result.Add(new(await View(state, company, token), company.CreatedByAccountId, null));
            RequireFresh(identity, actor);
            return result;
        }, ct);

    public Task<ReviewView> ReviewDetail(ActorRequest actor, Guid companyId, CancellationToken ct) =>
        Authorized(actor, "platform.companies.read", async (state, identity, token) =>
        {
            var company = await state.LockCompany(companyId, token) ?? throw Failure("not_found", 404);
            RequireFresh(identity, actor);
            var decision = company.CurrentApplicationId is { } id ? await state.Decision(id, token) : null;
            return new ReviewView(await View(state, company, token), company.CreatedByAccountId, decision?.InternalNote);
        }, ct);

    public Task<WorkspaceView> Workspace(ActorRequest actor, Guid companyId, CancellationToken ct) =>
        Authorized(actor, "organization.account", async (state, identity, token) =>
        {
            var company = await state.LockCompany(companyId, token) ?? throw Failure("not_found", 404);
            RequireFresh(identity, actor);
            if (company.Status != CompanyStatus.Approved) throw Failure("not_found", 404);
            var member = (await state.Memberships(identity.Identity.AccountId, token)).SingleOrDefault(m => m.CompanyId == companyId);
            if (member?.Status != MembershipStatus.Active) throw Failure("not_found", 404);
            var permissions = await state.Permissions(companyId, member.Id, token);
            if (!permissions.Contains(WorkspacePermission, StringComparer.Ordinal)) throw Failure("not_found", 404);
            return new WorkspaceView(companyId, company.Version, company.Name, member.Id,
                identity.Identity.AccountId, identity.Identity.AccountSessionId, permissions);
        }, ct);

    private Task<T> Authorized<T>(ActorRequest actor, string action,
        Func<ICompanyState, OrganizationAuthority, CancellationToken, Task<T>> work, CancellationToken ct) =>
        transactions.Run(token => authority.Validate(actor, action, token), work, ct);

    private void RequireFresh(OrganizationAuthority evidence, ActorRequest actor)
    {
        var now = clock.GetUtcNow();
        if (evidence.Identity.AccountSessionId != actor.AccountSessionId || evidence.ValidatedAt < now.AddSeconds(-10) ||
            evidence.ValidatedAt > now.AddSeconds(2) || evidence.Identity.ExpiresAt <= now || evidence.Identity.IdleExpiresAt <= now)
            throw Failure("authority_expired", 503);
    }

    private async Task<T?> Replay<T>(ICompanyState state, Guid actor, string operation, Guid target, string key, string hash, CancellationToken ct) where T : class
    {
        var receipt = await state.Receipt(actor, operation, target, key, ct);
        if (receipt is null) return null;
        if (receipt.ExpiresAt <= clock.GetUtcNow()) { state.RemoveReceipt(receipt); await state.Save(ct); return null; }
        if (receipt.PayloadHash != hash) throw Failure("idempotency_payload_conflict", 409);
        return JsonSerializer.Deserialize<T>(receipt.ResultJson) ?? throw Failure("organization_unavailable", 503);
    }

    private void Receipt<T>(ICompanyState state, OrganizationAuthority identity, string operation, Guid target, string key, string hash, T result) =>
        state.Add(new OperationReceipt(identity.Identity.AccountId, operation, target, key, hash, JsonSerializer.Serialize(result), clock.GetUtcNow().AddDays(7)));
    private static string Hash<T>(T payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw Failure("invalid_idempotency_key", 422);
    }
    private static void Own(Company company, OrganizationAuthority identity)
    { if (company.CreatedByAccountId != identity.Identity.AccountId) throw Failure("not_found", 404); }
    private static void Version(Company company, Guid version)
    { if (company.Version != version) throw Failure("company_version_conflict", 412); }
    private static CompanyApplication NewApplication(Company company, CompanyForm form, int submission, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), company.Id, company.CreatedByAccountId, submission, form.CompanyName, form.CountryCode,
            form.BusinessDescription, form.ApplicantRoleDescription, form.Website, form.LegalRegistrationId, now, Guid.CreateVersion7());
    private static CompanyForm Form(CompanyApplication application) => new(application.CompanyName, application.CountryCode,
        application.BusinessDescription, application.ApplicantRoleDescription, application.Website, application.LegalRegistrationId);
    private static async Task<ApplicationView> View(ICompanyState state, Company company, CancellationToken ct)
    {
        var application = company.CurrentApplicationId is { } id ? await state.Application(id, ct) : null;
        if (application is null) throw Failure("organization_unavailable", 503);
        var decision = await state.Decision(application.Id, ct);
        return new(company.Id, company.Version, company.Status.ToString(), application.Id, application.Version,
            application.SubmissionNumber, Form(application), decision?.ApplicantVisibleReason, application.SubmittedAt);
    }
    private static void Audit(ICompanyState state, Company company, CompanyApplication application, OrganizationAuthority identity,
        string name, Guid? prior, string correlation, DateTimeOffset now) => state.Add(new CompanyAudit(Guid.CreateVersion7(),
            company.Id, application.Id, identity.Identity.AccountId, name, prior, company.Version, identity.EvidenceId, now, correlation));

    private static CompanyForm Normalize(CompanyForm form)
    {
        if (form is null) throw Failure("invalid_form", 422);
        var normalized = new CompanyForm(form.CompanyName?.Trim() ?? "", form.CountryCode?.Trim().ToUpperInvariant() ?? "",
            form.BusinessDescription?.Trim() ?? "", form.ApplicantRoleDescription?.Trim() ?? "",
            string.IsNullOrWhiteSpace(form.Website) ? null : form.Website.Trim(),
            string.IsNullOrWhiteSpace(form.LegalRegistrationId) ? null : form.LegalRegistrationId.Trim());
        if (normalized.CompanyName.Length is < 1 or > 200 || normalized.BusinessDescription.Length is < 20 or > 2000 ||
            normalized.ApplicantRoleDescription.Length is < 2 or > 200 || !Countries.Contains(normalized.CountryCode) ||
            normalized.LegalRegistrationId?.Length > 200 || normalized.Website?.Length > 2048 ||
            (normalized.Website is not null && (!Uri.TryCreate(normalized.Website, UriKind.Absolute, out var website) ||
                website.Scheme is not "https" and not "http" || website.UserInfo.Length > 0)))
            throw Failure("invalid_form", 422);
        return normalized;
    }
    private static readonly HashSet<string> Countries = new(("AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW").Split(' '), StringComparer.Ordinal);
    private static CompanyFailure Failure(string code, int status) => new(code, status);
}
