using SqlGraphCopilot.Core.Parsing;

namespace SqlGraphCopilot.Core.Tests.Parsing;

public sealed class SqlAstVisitorTests
{
    private readonly TSqlParsingService _parser = new();

    private static IEnumerable<string> Names(IEnumerable<SqlObjectName> names) => names.Select(n => n.ToString());

    [Fact]
    public void Update_con_alias_resuelve_la_tabla_real_y_no_la_cuenta_como_lectura_del_alias()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("""
            CREATE PROCEDURE dbo.sp_ActualizarCliente AS
            UPDATE c SET c.Nombre = 'x'
            FROM dbo.Clientes c
            INNER JOIN dbo.Pedidos p ON p.ClienteId = c.Id;
            """);

        Assert.Equal("dbo.sp_ActualizarCliente", result.ObjectName);
        Assert.Contains("dbo.Clientes", Names(result.Writes));
        Assert.DoesNotContain("c", Names(result.Writes));
        Assert.Contains("dbo.Pedidos", Names(result.Reads));
        Assert.DoesNotContain("c", Names(result.Reads));
    }

    [Fact]
    public void Insert_select_marca_destino_como_escritura_y_no_como_lectura()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("""
            CREATE PROCEDURE dbo.sp_Log AS
            INSERT INTO dbo.LogAuditoria (Id) SELECT Id FROM dbo.Pedidos;
            """);

        Assert.Equal(new[] { "dbo.LogAuditoria" }, Names(result.Writes));
        Assert.Equal(new[] { "dbo.Pedidos" }, Names(result.Reads));
    }

    [Fact]
    public void Cte_tablas_temporales_y_variables_no_son_objetos()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("""
            CREATE PROCEDURE dbo.sp_Temp AS
            BEGIN
              DECLARE @t TABLE (Id INT);
              WITH Ultimos AS (SELECT Id FROM dbo.Pedidos)
              SELECT * INTO #tmp FROM Ultimos;
              INSERT INTO @t SELECT Id FROM #tmp;
              SELECT * FROM @t;
            END
            """);

        Assert.Equal(new[] { "dbo.Pedidos" }, Names(result.Reads));
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void Detecta_sql_dinamico()
    {
        Assert.True(_parser.ParseSqlScript("CREATE PROCEDURE dbo.a AS EXEC sp_executesql @sql;").HasDynamicSql);
        Assert.True(_parser.ParseSqlScript("CREATE PROCEDURE dbo.b AS EXEC (@sql);").HasDynamicSql);
        Assert.False(_parser.ParseSqlScript("CREATE PROCEDURE dbo.c AS EXEC dbo.sp_Otro;").HasDynamicSql);
    }

    [Fact]
    public void Conserva_nombres_de_tres_y_cuatro_partes()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("""
            CREATE PROCEDURE dbo.sp_Sincronizar AS
            BEGIN
              EXEC OtraBase.dbo.sp_Remoto;
              UPDATE LNK_OTHER.Inventario.dbo.Stock SET Cantidad = 0;
            END
            """);

        SqlObjectName executed = Assert.Single(result.Executes);
        Assert.Equal("OtraBase", executed.Database);
        SqlObjectName written = Assert.Single(result.Writes);
        Assert.Equal("LNK_OTHER", written.Server);
        Assert.Contains("LNK_OTHER", result.LinkedServers);
    }

    [Fact]
    public void Detecta_openquery()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("CREATE VIEW dbo.v AS SELECT * FROM OPENQUERY(LNK_CORE, 'SELECT 1 AS x');");

        Assert.True(result.UsesOpenQuery);
        Assert.Contains("LNK_CORE", result.LinkedServers);
    }

    [Fact]
    public void Select_into_truncate_y_merge_son_escrituras()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("""
            CREATE PROCEDURE dbo.sp_Cierre AS
            BEGIN
              SELECT * INTO dbo.Respaldo FROM dbo.Saldos;
              TRUNCATE TABLE dbo.Saldos;
              MERGE dbo.Destino AS d USING dbo.Origen AS o ON d.Id = o.Id
              WHEN MATCHED THEN UPDATE SET d.Valor = o.Valor;
            END
            """);

        Assert.Contains("dbo.Respaldo", Names(result.Writes));
        Assert.Contains("dbo.Saldos", Names(result.Writes));
        Assert.Contains("dbo.Destino", Names(result.Writes));
        Assert.Contains("dbo.Origen", Names(result.Reads));
    }

    [Fact]
    public void Funciones_escalares_y_de_tabla_son_llamadas()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("""
            CREATE PROCEDURE dbo.sp_Calc AS
            SELECT dbo.fn_Tasa(1), t.* FROM dbo.tvf_Movimientos(1) AS t;
            """);

        Assert.Contains("dbo.fn_Tasa", Names(result.Calls));
        Assert.Contains("dbo.tvf_Movimientos", Names(result.Calls));
    }

    [Fact]
    public void Create_or_alter_con_esquema_propio()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("CREATE OR ALTER PROCEDURE ventas.sp_A AS SELECT 1;");
        Assert.Equal("ventas.sp_A", result.ObjectName);
    }

    [Fact]
    public void Errores_de_sintaxis_no_lanzan_excepcion()
    {
        SqlAnalysisResult result = _parser.ParseSqlScript("CREATE PROCEDURE dbo.x AS SELEC * FROM");
        Assert.True(result.HasErrors);
    }
}
