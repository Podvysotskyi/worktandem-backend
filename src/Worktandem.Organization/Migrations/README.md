# Organization-owned migrations

Reserved for this service's future persistence migrations. No database, migration
engine, or connection is configured. Choose persistence before adding migrations.
Other services must not read this database directly or share its DbContext/entities.
