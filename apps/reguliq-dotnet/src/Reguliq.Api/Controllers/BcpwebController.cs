using Microsoft.AspNetCore.Mvc;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("bcpweb")]
public class BcpwebController(DashboardService dashboard) : ControllerBase
{
    [HttpGet("dashboard")]
    public ActionResult<ApiResponse<DashboardMetricsDto>> GetDashboard() =>
        Ok(new ApiResponse<DashboardMetricsDto>(true, dashboard.GetSeedMetrics()));
}
