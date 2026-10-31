
- Changed: `Encrypt` and `TrustServerCertificate` written in a connection string are no longer overridden by the `SqlServer` option defaults. An explicit `sslMode` still applies.
- Fixed: `SqlFn.BucketUtc` works for dates after January 2038. Before, SQL Server failed with an arithmetic overflow (error 8115), because `DATEADD` takes an `int` number of seconds.
