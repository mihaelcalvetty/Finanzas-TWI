using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Api.Controllers;

[Route("api/auth/slack")]
public class SlackAuthController(IAuthenticationSchemeProvider schemes) : ExternalAuthControllerBase(schemes)
{
    protected override string ProviderName => "Slack";
    protected override string ProviderScheme => "Slack";
}
