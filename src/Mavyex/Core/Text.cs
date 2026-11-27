namespace Mavyex.Core;

/// <summary>Formatting and comparison helpers (model.rs fmt_size / fmt_time / natural_cmp, actions.rs fuzzy).</summary>
public static class Text
{
    /// <summary>Case-insensitive "natural" comparison: file2 &lt; file10.</summary>
    public static int NaturalCmp(string a, string b)
    {
        int i = 0, j = 0;
        while (true)
        {
            if (i >= a.Length && j >= b.Length) return 0;
            if (i >= a.Length) return -1;
            if (j >= b.Length) return 1;
            char x = a[i], y = b[j];
            if (char.IsAsciiDigit(x) && char.IsAsciiDigit(y))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var ta = a[si..i].TrimStart('0');
                var tb = b[sj..j].TrimStart('0');
                int o = ta.Length.CompareTo(tb.Length);
                if (o == 0) o = string.CompareOrdinal(ta, tb);
                if (o != 0) return Math.Sign(o);
            }
            else
            {
                int o = string.CompareOrdinal(char.ToLowerInvariant(x).ToString(), char.ToLowerInvariant(y).ToString());
                if (o != 0) return Math.Sign(o);
                i++;
                j++;
            }
        }
    }

    public static readonly IComparer<string> Natural = Comparer<string>.Create(NaturalCmp);

    public static string FmtSize(long n)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        if (n < 1024) return $"{n} B";
        double v = n;
        int k = 0;
        while (v >= 1024 && k < u.Length - 1)
        {
            v /= 1024;
            k++;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v:0.0} {u[k]}");
    }

    /// <summary>"2024-05-01 13:45" in local time, "" when unknown.</summary>
    public static string FmtTime(DateTime? utc) =>
        utc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Subsequence fuzzy match used by the command palette (actions.rs fuzzy). Every whitespace separated token
    /// must match; higher is better; null = no match.</summary>
    public static int? Fuzzy(string query, string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Length != text.Length)
            return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(t => lower.Contains(t.ToLowerInvariant())) ? 10 : null;
        int total = 0;
        foreach (var tok in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var q = tok.ToLowerInvariant();
            int qi = 0, score = 0, last = -2;
            for (int i = 0; i < lower.Length; i++)
            {
                if (qi < q.Length && lower[i] == q[qi])
                {
                    int s = 10;
                    if (last + 1 == i) s += 15;
                    if (i == 0 || !char.IsLetterOrDigit(text[i - 1]) || (char.IsUpper(text[i]) && char.IsLower(text[i - 1]))) s += 12;
                    score += s;
                    last = i;
                    qi++;
                }
            }
            if (qi < q.Length) return null;
            if (lower.Contains(q)) score += 30;
            total += score;
        }
        return total - lower.Length / 6;
    }
}
