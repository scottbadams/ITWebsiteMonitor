using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebsiteMonitor.App.Pages.Monitor;

public sealed class IndexModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string InstanceId { get; set; } = "";
}
