using Microsoft.AspNetCore.Mvc;

namespace PbiVisualExport.Web.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
