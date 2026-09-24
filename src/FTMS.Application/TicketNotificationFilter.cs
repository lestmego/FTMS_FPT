using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FTMS.Domain;

namespace FTMS.Application;

public static class TicketNotificationFilter
{
    private const string RequiredDepartment = "toc phong dich vu data center";

    public static bool ShouldNotify(TicketEvent item, TicketSnapshot? previous)
    {
        var ticket = item.Snapshot;
        var department = !string.IsNullOrWhiteSpace(ticket.DepartmentName)
            ? ticket.DepartmentName : previous?.DepartmentName;
        return NormalizeDepartment(department) == RequiredDepartment;
    }

    private static string NormalizeDepartment(string? value)
    {
        var formD = (value ?? string.Empty).ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var text = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            text.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
    }
}
