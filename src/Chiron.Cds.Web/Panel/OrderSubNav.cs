using System.Net;
using System.Text;

namespace Chiron.Cds.Web.Panel;

/// <summary>
/// Pill-strip sub-nav rendered at the top of every order sub-page so the
/// doctor can move between Medication / Lab / Imaging without leaving the
/// Orders chart-tab. The Orders chart-tab stays active for all three.
/// </summary>
internal static class OrderSubNav
{
    public static void Render(StringBuilder sb, string patientId, OrderSubNavActive active)
    {
        ArgumentNullException.ThrowIfNull(sb);
        var escaped = Uri.EscapeDataString(patientId);
        sb.Append("<nav class=\"order-subnav\" aria-label=\"Order types\">");
        Tab(sb, $"/app/patient/{escaped}/orders", "Medication", active == OrderSubNavActive.Medication);
        Tab(sb, $"/app/patient/{escaped}/orders/labs", "Labs", active == OrderSubNavActive.Labs);
        Tab(sb, $"/app/patient/{escaped}/orders/imaging", "Imaging", active == OrderSubNavActive.Imaging);
        sb.Append("</nav>");
    }

    private static void Tab(StringBuilder sb, string href, string label, bool isActive)
    {
        sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(href)).Append("\"");
        if (isActive) sb.Append(" class=\"active\"");
        sb.Append(">").Append(WebUtility.HtmlEncode(label)).Append("</a>");
    }
}

public enum OrderSubNavActive { Medication, Labs, Imaging }
