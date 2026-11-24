using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Api.Controllers;

[Route("api/auth/x")]
public class XAuthController(IAuthenticationSchemeProvider schemes) : ExternalAuthControllerBase(schemes)
{
    protected override string ProviderName => "X";
    protected override string ProviderScheme => "X";
}
