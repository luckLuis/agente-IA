using System.Text;

namespace SqlGraphCopilot.Application.Agent;

/// <summary>
/// Reduce el T-SQL que se envía al modelo SIN cambiar su significado: quita comentarios, líneas en blanco,
/// espacios finales y reduce la sangría a la mitad. Respeta cadenas '...', identificadores [..] y "..".
/// Menos texto = menos tokens (y menos latencia).
/// </summary>
public static class SqlMinifier
{
    public static string Minify(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;

        StringBuilder sb = new(sql.Length);
        int i = 0;
        int n = sql.Length;
        bool atLineStart = true;
        int indent = 0;

        while (i < n)
        {
            char c = sql[i];

            // Comentario de línea: se descarta hasta el fin de la línea.
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n' && sql[i] != '\r') i++;
                continue;
            }

            // Comentario de bloque (T-SQL los permite anidados).
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                int depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (!atLineStart) sb.Append(' ');   // evita pegar dos palabras: SELECT/**/1
                continue;
            }

            if (c == '\r') { i++; continue; }

            if (c == '\n')
            {
                TrimEnd(sb);
                if (!atLineStart) sb.Append('\n');   // las líneas vacías (o solo comentario) desaparecen
                atLineStart = true;
                indent = 0;
                i++;
                continue;
            }

            if (atLineStart && (c == ' ' || c == '\t'))
            {
                indent += c == '\t' ? 4 : 1;
                i++;
                continue;
            }

            if (atLineStart)
            {
                sb.Append(' ', Math.Min(indent / 2, 16));
                atLineStart = false;
            }

            switch (c)
            {
                case '\'':
                    i = CopyQuoted(sql, i, '\'', sb);
                    break;
                case '"':
                    i = CopyQuoted(sql, i, '"', sb);
                    break;
                case '[':
                    i = CopyQuoted(sql, i, ']', sb, opener: '[');
                    break;
                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }

        TrimEnd(sb);
        return sb.ToString();
    }

    /// <summary>Copia un literal o identificador entre delimitadores tal cual (con el delimitador duplicado como escape).</summary>
    private static int CopyQuoted(string sql, int start, char close, StringBuilder sb, char? opener = null)
    {
        int n = sql.Length;
        sb.Append(opener ?? close);
        int i = start + 1;
        while (i < n)
        {
            char c = sql[i];
            sb.Append(c);
            i++;
            if (c == close)
            {
                if (i < n && sql[i] == close) { sb.Append(sql[i]); i++; continue; }   // '' o ]] escapado
                break;
            }
        }
        return i;
    }

    private static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t')) sb.Length--;
    }
}
