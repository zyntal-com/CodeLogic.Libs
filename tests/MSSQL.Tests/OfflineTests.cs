using System.Data;
using System.Reflection;
using CL.MSSQL.Configuration;
using CL.MSSQL.Core;
using CL.MSSQL.Models;
using CL.MSSQL.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace MSSQL.Tests;

public sealed class OfflineTests
{
    [Fact]
    public void Connection_string_override_is_authoritative_and_structured_modes_work()
    {
        const string secret = "Server=tcp:example.database.windows.net;Authentication=Active Directory Default;Database=app;Encrypt=True";
        var overridden = new SqlServerDatabaseConfig { ConnectionString = secret, Host = "ignored", Password = "ignored" };
        Assert.Equal(secret, overridden.BuildConnectionString());

        var integrated = new SqlServerDatabaseConfig
        {
            Host = "dbhost", Instance = "SQLEXPRESS", Database = "app",
            AuthenticationMode = SqlServerAuthenticationMode.IntegratedSecurity,
            TrustServerCertificate = true
        };
        var parsed = new SqlConnectionStringBuilder(integrated.BuildConnectionString());
        Assert.Equal("dbhost\\SQLEXPRESS", parsed.DataSource);
        Assert.True(parsed.IntegratedSecurity);
        Assert.True(parsed.Encrypt);

        var login = new SqlServerDatabaseConfig { Host = "dbhost", Port = 1444, Database = "app", Username = "sa", Password = "secret" };
        parsed = new(login.BuildConnectionString());
        Assert.Equal("dbhost,1444", parsed.DataSource);
        Assert.Equal("sa", parsed.UserID);
        Assert.Equal("secret", parsed.Password);

        var invalid = new SqlServerDatabaseConfig
        {
            Database = "app", Username = "sa", MinPoolSize = 10, MaxPoolSize = 5,
            TransientRetryCount = 256, MaxBatchInsertSize = 0
        }.Validate();
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, error => error.Contains("MaxPoolSize", StringComparison.Ordinal));
    }

    [Fact]
    public void Dialect_quotes_identifiers_escapes_like_and_caps_parameters()
    {
        Assert.Equal("[odd]]name]", SqlServerDialect.Quote("odd]name"));
        Assert.Equal("[sales].[order]", SqlServerDialect.QuoteMultipart("sales.order"));
        Assert.Throws<ArgumentException>(() => SqlServerDialect.QuoteMultipart("sales..order"));
        Assert.Equal("100[%][_]x[[]", SqlServerDialect.EscapeLike("100%_x["));
        Assert.Equal(208, SqlServerDialect.MaxBatchRows(10));
    }

    [Fact]
    public void Native_type_inference_and_parameter_metadata_are_correct()
    {
        Assert.Equal(DataType.NVarChar, TypeConverter.InferColumn(typeof(string)).DataType);
        Assert.Equal(DataType.DateTime2, TypeConverter.InferColumn(typeof(DateTime)).DataType);
        Assert.Equal(DataType.UniqueIdentifier, TypeConverter.InferColumn(typeof(Guid)).DataType);
        Assert.Equal(DataType.VarBinaryMax, TypeConverter.InferColumn(typeof(byte[])).DataType);
        Assert.Equal(DataType.Int, TypeConverter.InferColumn(typeof(DayOfWeek)).DataType);

        var attr = new ColumnAttribute { DataType = DataType.Decimal, Precision = 19, Scale = 4 };
        var parameter = TypeConverter.CreateParameter("@amount", 12.5m, attr);
        Assert.Equal(SqlDbType.Decimal, parameter.SqlDbType);
        Assert.Equal(19, parameter.Precision);
        Assert.Equal(4, parameter.Scale);

        var geography = TypeConverter.CreateParameter("@point", Array.Empty<byte>(),
            new ColumnAttribute { DataType = DataType.Geography });
        Assert.Equal(SqlDbType.Udt, geography.SqlDbType);
        Assert.Equal("geography", geography.UdtTypeName);
    }

    [Fact]
    public void Sql_server_and_azure_transient_error_numbers_are_classified()
    {
        Assert.True(ConnectionManager.IsTransientNumber(1205));
        Assert.True(ConnectionManager.IsTransientNumber(1222));
        Assert.True(ConnectionManager.IsTransientNumber(40501));
        Assert.True(ConnectionManager.IsTransientNumber(40613));
        Assert.False(ConnectionManager.IsTransientNumber(2627));
    }

    [Fact]
    public void Predicates_use_bit_comparisons_and_escape_like_wildcards()
    {
        var (booleanSql, _) = SqlServerExpressionVisitor.Translate<OfflineEntity>(x => x.Enabled && x.Id > 2);
        Assert.Contains("[enabled] = 1", booleanSql);
        var (likeSql, parameters) = SqlServerExpressionVisitor.Translate<OfflineEntity>(x => x.Name.Contains("50%_["));
        Assert.Contains("LIKE", likeSql);
        Assert.Equal("%50[%][_][[]%", parameters["@p0"]);
    }

    [Fact]
    public void Schema_ddl_uses_sql_server_native_objects()
    {
        var ddl = new SchemaAnalyzer().GenerateCreateTable(typeof(OfflineEntity));
        Assert.Contains("CREATE TABLE [audit].[offline]", ddl);
        Assert.Contains("IDENTITY(1,1)", ddl);
        Assert.Contains("nvarchar(100)", ddl);
        Assert.Contains("COLLATE Latin1_General_100_CI_AS", ddl);
        Assert.Contains("ISJSON([payload]) = 1", ddl);
        Assert.Contains("INCLUDE ([payload])", ddl);
        Assert.DoesNotContain("ENGINE=", ddl);
    }

    [Fact]
    public void Public_api_tracks_mysql2_except_documented_native_types()
    {
        var mysqlTypes = typeof(CL.MySQL2.MySQL2Library).Assembly.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("CL.MySQL2", StringComparison.Ordinal) == true)
            .Where(type => !NativeTypeExclusions.Contains(type.Name))
            .ToDictionary(NormalizedTypeName, StringComparer.Ordinal);
        var sqlServerTypes = typeof(CL.MSSQL.MSSQLLibrary).Assembly.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("CL.MSSQL", StringComparison.Ordinal) == true)
            .Where(type => !NativeTypeExclusions.Contains(type.Name))
            .ToDictionary(NormalizedTypeName, StringComparer.Ordinal);

        Assert.Empty(mysqlTypes.Keys.Except(sqlServerTypes.Keys));
        Assert.Empty(sqlServerTypes.Keys.Except(mysqlTypes.Keys));
        foreach (var name in mysqlTypes.Keys)
            AssertMemberSignaturesMatch(mysqlTypes[name], sqlServerTypes[name]);
    }

    private static readonly HashSet<string> NativeTypeExclusions =
    [
        "MySqlDatabaseConfig", "SqlServerDatabaseConfig", "SqlServerAuthenticationMode",
        "DataType", "StorageType", "TableEngine", "Charset", "TableAttribute", "ColumnAttribute"
    ];

    // Member signatures that differ on purpose. SQL Server has no per-database character set,
    // so CreateDatabaseAsync (library facade and DatabaseAdmin) drops MySQL's charset parameter.
    private static readonly HashSet<string> NativeMemberExclusions =
    [
        "M:CreateDatabaseAsync`0(System.String,System.String,System.String,System.Boolean,System.String,System.Threading.CancellationToken):System.Threading.Tasks.Task`1[CodeLogic.Core.Results.Result`1[System.Boolean]]",
        "M:CreateDatabaseAsync`0(System.String,System.String,System.Boolean,System.String,System.Threading.CancellationToken):System.Threading.Tasks.Task`1[CodeLogic.Core.Results.Result`1[System.Boolean]]"
    ];

    private static string NormalizedTypeName(Type type) => type.Name
        .Replace("MySQL2Library", "DatabaseLibrary", StringComparison.Ordinal)
        .Replace("MSSQLLibrary", "DatabaseLibrary", StringComparison.Ordinal)
        .Replace("MySQL2Strings", "DatabaseStrings", StringComparison.Ordinal)
        .Replace("MSSQLStrings", "DatabaseStrings", StringComparison.Ordinal);

    private static void AssertMemberSignaturesMatch(Type expected, Type actual)
    {
        static string TypeName(Type type) => type.ToString()
            .Replace("CL.MySQL2", "CL.MSSQL", StringComparison.Ordinal)
            .Replace("MySQL2Library", "MSSQLLibrary", StringComparison.Ordinal)
            .Replace("MySQL2Strings", "MSSQLStrings", StringComparison.Ordinal)
            .Replace("MySqlDatabaseConfig", "SqlServerDatabaseConfig", StringComparison.Ordinal)
            .Replace("MySqlConnector.MySqlConnection", "Microsoft.Data.SqlClient.SqlConnection", StringComparison.Ordinal)
            .Replace("MySqlConnector.MySqlCommand", "Microsoft.Data.SqlClient.SqlCommand", StringComparison.Ordinal)
            .Replace("MySqlConnector.MySqlTransaction", "Microsoft.Data.SqlClient.SqlTransaction", StringComparison.Ordinal);
        static HashSet<string> Signatures(Type type)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var constructor in type.GetConstructors(flags))
                signatures.Add($"C({string.Join(',', constructor.GetParameters().Select(p => TypeName(p.ParameterType)))})");
            foreach (var method in type.GetMethods(flags).Where(m => !m.IsSpecialName))
                signatures.Add($"M:{method.Name}`{method.GetGenericArguments().Length}({string.Join(',', method.GetParameters().Select(p => TypeName(p.ParameterType)))}):{TypeName(method.ReturnType)}");
            foreach (var property in type.GetProperties(flags))
                signatures.Add($"P:{property.Name}:{TypeName(property.PropertyType)}");
            foreach (var eventInfo in type.GetEvents(flags))
                signatures.Add($"E:{eventInfo.Name}:{TypeName(eventInfo.EventHandlerType!)}");
            return signatures;
        }
        var expectedMembers = Signatures(expected);
        var actualMembers = Signatures(actual);
        expectedMembers.ExceptWith(NativeMemberExclusions);
        actualMembers.ExceptWith(NativeMemberExclusions);
        Assert.True(expectedMembers.SetEquals(actualMembers),
            $"API mismatch for {expected.Name}. Missing: {string.Join("; ", expectedMembers.Except(actualMembers))}. Extra: {string.Join("; ", actualMembers.Except(expectedMembers))}.");
    }

    [Table(Name = "offline", Schema = "audit", Comment = "offline entity")]
    private sealed class OfflineEntity
    {
        [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
        [Column(Name = "name", DataType = DataType.NVarChar, Size = 100, NotNull = true, Collation = "Latin1_General_100_CI_AS")]
        [Index(Include = [nameof(Payload)])]
        public string Name { get; set; } = "";
        [Column(Name = "enabled", DataType = DataType.Bit)] public bool Enabled { get; set; }
        [Column(Name = "payload", DataType = DataType.Json)] public string? Payload { get; set; }
    }
}
