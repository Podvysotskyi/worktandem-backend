namespace Worktandem.Organization.Domain;

public sealed class CompanyApplication(Guid id, Guid companyId, Guid applicantAccountId, int submissionNumber, string companyName, string countryCode, string businessDescription, string applicantRoleDescription, string? website, string? legalRegistrationId, DateTimeOffset submittedAt, Guid version)
{
    public Guid Id { get; private set; } = id;
    public Guid CompanyId { get; private set; } = companyId;
    public Guid ApplicantAccountId { get; private set; } = applicantAccountId;
    public int SubmissionNumber { get; private set; } = submissionNumber;
    public string CompanyName { get; private set; } = companyName;
    public string CountryCode { get; private set; } = countryCode;
    public string BusinessDescription { get; private set; } = businessDescription;
    public string ApplicantRoleDescription { get; private set; } = applicantRoleDescription;
    public string? Website { get; private set; } = website;
    public string? LegalRegistrationId { get; private set; } = legalRegistrationId;
    public DateTimeOffset SubmittedAt { get; private set; } = submittedAt;
    public Guid Version { get; private set; } = version;
}
