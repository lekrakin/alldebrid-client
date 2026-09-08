using AdbClient.Data.Enums;
using AdbClient.Service.Services;
using AdbClient.Web.Models.Requests;
using AdbClient.Web.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdbClient.Web.Controllers;

[Route("Api/Authentication")]
public class AuthController(Authentication authentication, Settings settings) : Controller
{
    [AllowAnonymous]
    [Route("IsLoggedIn")]
    [HttpGet]
    public async Task<ActionResult> IsLoggedIn()
    {
        if (Settings.Get.General.AuthenticationType == AuthenticationType.None)
        {
            return Ok();
        }

        if (User.Identity?.IsAuthenticated == false)
        {
            var user = await authentication.GetUser();

            if (user == null)
            {
                return StatusCode(402, "Setup required");
            }

            return StatusCode(403);
        }

        return Ok();
    }

    [AllowAnonymous]
    [Route("Create")]
    [HttpPost]
    public async Task<ActionResult<AuthControllerCreateResponse>> Create([FromBody] AuthControllerLoginRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        var user = await authentication.GetUser();

        if (user != null)
        {
            return StatusCode(401);
        }

        if (string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest("Invalid UserName or Password");
        }

        var registerResult = await authentication.Register(request.UserName, request.Password);

        if (!registerResult.Succeeded)
        {
            return BadRequest(registerResult.Errors.First().Description);
        }

        await authentication.Login(request.UserName, request.Password);

        return Ok(new AuthControllerCreateResponse(
            !string.IsNullOrWhiteSpace(Settings.Get.Provider.ApiKey)));
    }

    [Authorize(Policy = "AuthSetting")]
    [Route("SetupProvider")]
    [HttpPost]
    public async Task<ActionResult> SetupProvider([FromBody] AuthControllerSetupProviderRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (!string.IsNullOrEmpty(Settings.Get.Provider.ApiKey))
        {
            return StatusCode(401);
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest("Invalid AllDebrid API key");
        }

        await settings.Update("Provider:ApiKey", request.Token);

        return Ok();
    }

    [AllowAnonymous]
    [Route("Login")]
    [HttpPost]
    public async Task<ActionResult> Login([FromBody] AuthControllerLoginRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        var user = await authentication.GetUser();

        if (user == null)
        {
            return StatusCode(402);
        }

        if (string.IsNullOrEmpty(request.UserName) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest("Invalid credentials");
        }

        var result = await authentication.Login(request.UserName, request.Password);

        if (!result.Succeeded)
        {
            return BadRequest("Invalid credentials");
        }

        return Ok();
    }

    [Route("Logout")]
    [HttpPost]
    public async Task<ActionResult> Logout()
    {
        await authentication.Logout();
        return Ok();
    }

    [Route("Update")]
    [HttpPost]
    [Authorize(Policy = "AuthSetting")]
    public async Task<ActionResult> Update([FromBody] AuthControllerUpdateRequest? request)
    {
        if (request == null)
        {
            return BadRequest();
        }

        if (string.IsNullOrWhiteSpace(request.UserName) && string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Enter a new username, password, or both.");
        }

        var updateResult = await authentication.Update(request.UserName, request.Password);

        if (!updateResult.Succeeded)
        {
            return BadRequest(updateResult.Errors.First().Description);
        }

        return Ok();
    }
}
