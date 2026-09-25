using Microsoft.AspNetCore.Mvc;

namespace PbiVisualExport.SampleHost.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
