using System;
using System.Globalization;

namespace KhaozEngine.BugReports.Contracts;

public static class ReportIds
{
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewId() => Guid.CreateVersion7().ToString("N");

    public static bool IsValid(string reportId)
    {
        if (reportId is null || reportId.Length != 32 || reportId[12] != '7' || reportId[16] is not ('8' or '9' or 'a' or 'b'))
            return false;
        foreach (char c in reportId)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    public static string ReferenceCode(string reportId)
    {
        if (!IsValid(reportId))
            throw new ArgumentException("A canonical version 7 report id is required.", nameof(reportId));
        uint bits = uint.Parse(reportId.AsSpan(24), NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0x3FFFFFFF;
        Span<char> code = stackalloc char[8];
        code[0] = 'R';
        code[1] = '-';
        for (int i = 0; i < 6; i++)
            code[i + 2] = Alphabet[(int)((bits >> ((5 - i) * 5)) & 31)];
        return new string(code);
    }
}
