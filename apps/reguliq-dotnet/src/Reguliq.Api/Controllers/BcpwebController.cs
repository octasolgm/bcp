using Microsoft.AspNetCore.Mvc;
using Reguliq.Api.Models;
using Reguliq.Api.Services;

namespace Reguliq.Api.Controllers;

[ApiController]
[Route("bcpweb")]
public class BcpwebController(DashboardService dashboard) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<DashboardMetricsDto>>> GetDashboard(CancellationToken ct) =>
        Ok(new ApiResponse<DashboardMetricsDto>(true, await dashboard.GetMetricsAsync(ct)));
}
