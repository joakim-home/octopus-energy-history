using System.Text.Json;
using JoakimHomeDashboard.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace JoakimHomeDashboard.Web.Pages;

public sealed record IssuedAllocationCharge(string Issued, string Title, string From, string To, string Quantity, decimal Net, decimal Tax, decimal Gross);
public sealed class AllocationsModel(SupplierAllocationStore store) : PageModel
{
    public IReadOnlyList<AllocationAudit> Days { get; private set; }=[];
    public SupplierSyncHealth? Health { get; private set; }
    public List<IssuedAllocationCharge> Charges { get; }=[];
    public int SmartSessions { get; private set; }
    public int BoostSessions { get; private set; }
    public async Task OnGetAsync()
    {
        Days=await store.AuditAsync(false,HttpContext.RequestAborted);
        Health=await store.GetHealthAsync(HttpContext.RequestAborted);
        foreach(var evidence in await store.ReadEvidenceAsync(HttpContext.RequestAborted))
        {
            if(evidence.Kind=="issued_bills")
            {
                using var doc=JsonDocument.Parse(evidence.Payload);
                foreach(var bill in doc.RootElement.GetProperty("data").GetProperty("account").GetProperty("bills").GetProperty("edges").EnumerateArray())
                {
                    var b=bill.GetProperty("node"); if(!b.TryGetProperty("transactions",out var ts)) continue;
                    foreach(var transaction in ts.GetProperty("edges").EnumerateArray())
                    {
                        var t=transaction.GetProperty("node");
                        if(t.GetProperty("__typename").GetString()!="Charge"||!t.GetProperty("isIssued").GetBoolean()||t.GetProperty("isReversed").GetBoolean()) continue;
                        var amount=t.GetProperty("amounts"); var consumption=t.GetProperty("consumption");
                        if(consumption.ValueKind==JsonValueKind.Null) continue;
                        Charges.Add(new(b.GetProperty("issuedDate").GetString()??"",t.GetProperty("title").GetString()??"",consumption.GetProperty("startDate").GetString()??"",consumption.GetProperty("endDate").GetString()??"",consumption.GetProperty("quantity").ToString(),amount.GetProperty("net").GetDecimal()/100m,amount.GetProperty("tax").GetDecimal()/100m,amount.GetProperty("gross").GetDecimal()/100m));
                    }
                }
            }
            if(evidence.Kind=="charging_sessions")
            {
                using var doc=JsonDocument.Parse(evidence.Payload);
                foreach(var device in doc.RootElement.GetProperty("data").GetProperty("devices").EnumerateArray())
                    if(device.TryGetProperty("chargingSessions",out var sessions)&&sessions.ValueKind!=JsonValueKind.Null)
                        foreach(var edge in sessions.GetProperty("edges").EnumerateArray())
                        {
                            var type=edge.GetProperty("node").GetProperty("type").GetString();
                            if(type=="SMART") SmartSessions++; else if(type=="BOOST") BoostSessions++;
                        }
            }
        }
    }
}
