
- Native AOT: CL.Data.SqlServer itself is AOT-clean, but Microsoft.Data.SqlClient 7 still reports about 57 trim/AOT warnings (Azure authentication provider loading, UDTs, System.Configuration). Test SQL Server apps under AOT before relying on it.
