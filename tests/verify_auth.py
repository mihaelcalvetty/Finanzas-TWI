"""Pruebas HTTPS de los seis proveedores sin login real ni canje de códigos.

Requiere API compilada y certificado HTTPS de desarrollo. Las pruebas OIDC consultan
metadata pública. Inicia/detiene su propia API en un puerto libre; no lee secretos.
"""
import http.cookiejar
import json
import os
from pathlib import Path
import re
import ssl
import subprocess
import tempfile
import threading
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = Path(os.environ.get("FINANZAS_TEST_OUTPUT", str(ROOT / "src/FinanzasPersonales.Api/bin/Debug/net10.0")))
PROVIDERS = {
    "google": ("accounts.google.com", "/api/auth/google/callback", True),
    "discord": ("discord.com", "/api/auth/discord/callback", False),
    "twitch": ("id.twitch.tv", "/signin-twitch", False),
    "spotify": ("accounts.spotify.com", "/signin-spotify", True),
    "dropbox": ("www.dropbox.com", "/auth/dropbox/callback", True),
    "microsoft": ("login.microsoftonline.com", "/auth/microsoft/callback", True),
    "x": ("x.com", "/api/auth/x/callback", True),
    "slack": ("slack.com", "/api/auth/slack/callback", False),
}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def verify(configured, http_mode=False):
    env = os.environ.copy()
    env["ASPNETCORE_ENVIRONMENT"] = "Development"
    env["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information"
    env["USE_HTTP_COOKIES"] = "true" if http_mode else "false"
    env["DISABLE_HTTPS_REDIRECTION"] = "true" if http_mode else "false"
    env["CORS_ALLOWED_ORIGINS"] = "http://localhost:3000"
    temporary_root = Path("/tmp/opencode")
    keys = tempfile.TemporaryDirectory(prefix="oidc-test-", dir=temporary_root if temporary_root.exists() else None)
    env["DATA_PROTECTION_KEYS_PATH"] = keys.name
    for provider, (_, callback, _) in PROVIDERS.items():
        name = provider.capitalize()
        for prefix in ("Authentication", "Auth", "AUTH"):
            env[f"{prefix}__{name}__ClientId"] = "fixture-not-a-real-app" if configured else ""
            env[f"{prefix}__{name}__ClientSecret"] = "fixture-not-a-real-secret" if configured else ""
            env[f"{prefix}__{name}__CallbackPath"] = callback
        for key in ("CLIENTID", "CLIENTSECRET"):
            env[f"AUTH__{provider.upper()}__{key}"] = (
                "fixture-not-a-real-secret" if configured else "")
        env[f"AUTH__{provider.upper()}__CALLBACKPATH"] = callback
    env["Authentication__Microsoft__TenantId"] = "common"
    process = subprocess.Popen(
        ["dotnet", str(OUTPUT / "FinanzasPersonales.Api.dll"),
         "--contentRoot", str(ROOT / "src/FinanzasPersonales.Api"),
         "--urls", ("http" if http_mode else "https") + "://127.0.0.1:0"],
        # Evitar cargar el .env real: solo se usan fixtures explícitos del entorno.
        cwd=keys.name, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    ready = threading.Event()
    addresses = []
    logs = []

    def read_logs():
        for line in process.stdout:
            logs.append(line)
            if "Now listening on:" in line:
                addresses.append(re.search(r"https?://[^\s]+", line).group())
                ready.set()

    reader = threading.Thread(target=read_logs, daemon=True)
    reader.start()
    try:
        assert ready.wait(30), "La API no arrancó: revisar el certificado HTTPS y la configuración."
        base = addresses[0]
        jar = http.cookiejar.CookieJar()
        # Únicamente para el host local de pruebas, nunca para los proveedores.
        opener = urllib.request.build_opener(
            NoRedirect(), urllib.request.HTTPCookieProcessor(jar),
            urllib.request.HTTPSHandler(context=ssl._create_unverified_context()))

        def request(path, data=None):
            body = None if data is None else urllib.parse.urlencode(data).encode()
            try:
                response = opener.open(base + path, data=body, timeout=60)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                return response.code, response.headers, response.read().decode()

        for alias in ("/auth/login", "/auth/dev3"):
            status, headers, _ = request(alias)
            assert status == 302 and headers["Location"] == "/"
            assert "no-store" in headers.get("Cache-Control", "")
            status, headers, _ = request(alias + "?error=google-not-configured")
            assert status == 302 and headers["Location"] == "/?error=google-not-configured"
            assert request(alias + "?error=untrusted-text")[1]["Location"] == "/"
        status, _, page = request("/")
        assert status == 200 and 'id="auth-message"' in page
        for provider in PROVIDERS:
            if provider not in ("microsoft", "spotify"):
                assert "Iniciar sesión con " + provider.capitalize() in page
        assert "Iniciar sesión con Microsoft" not in page
        assert "Iniciar sesión con Spotify" not in page
        assert "Iniciar sesión con X (Twitter)" in page
        status, headers, body = request("/api/auth/providers")
        assert status == 200 and "no-store" in headers.get("Cache-Control", "")
        providers = json.loads(body)
        assert len(providers) == 8
        assert all(p["configured"] is configured for p in providers)
        assert all(set(p) == {"name", "configured"} for p in providers)
        csrf_response = request("/api/auth/csrf")
        assert csrf_response[0] == 200
        token = json.loads(csrf_response[2])["token"]
        # La página pudo emitir la cookie antes; comprobar el jar en ese caso.
        csrf_cookie = "Finanzas.Antiforgery" if http_mode else "__Host-Finanzas.Antiforgery"
        assert any(c.name == csrf_cookie and c.secure is not http_mode for c in jar)
        assert json.loads(request("/api/auth/me")[2])["isAuthenticated"] is False
        assert request("/api/auth/logout", {})[0] == 401

        for provider, (host, callback, pkce) in PROVIDERS.items():
            path = f"/api/auth/{provider}"
            assert request(path + "/user")[0] == 401
            assert request(path + "/login?returnUrl=https://evil.example/")[0] == 400
            assert request(path + "/login", {})[0] == 400
            assert request(path + "/login")[0] == 302
            frontend = "http://localhost:3000/?view=login#providers"
            status, headers, _ = request(path + "/login?" + urllib.parse.urlencode({"returnUrl": frontend}),
                                         {"__RequestVerificationToken": token})
            assert status == 302
            if not configured:
                assert headers["Location"] == f"/?error={provider}-not-configured"
                continue
            parts = urllib.parse.urlsplit(headers["Location"])
            query = urllib.parse.parse_qs(parts.query)
            assert parts.scheme == "https" and parts.netloc == host
            assert query["response_type"] == ["code"]
            assert query["redirect_uri"] == [base + callback]
            assert query.get("state")
            if provider == "x":
                assert len(query["state"][0]) <= 500
                assert set(query.get("scope", [""])[0].split()) == {"users.read", "tweet.read"}
            assert not any(key in query for key in ("client_secret", "access_token", "id_token"))
            if pkce:
                assert query.get("code_challenge_method") == ["S256"] and query.get("code_challenge")
            oidc = provider in ("google", "microsoft", "slack")
            if oidc:
                # En Code Flow query es el modo por defecto y el middleware puede omitirlo en la URL.
                assert query.get("nonce") and query.get("response_mode", ["query"]) == (["query"] if http_mode or provider == "slack" else ["form_post"])
                if provider == "slack":
                    assert parts.path == "/openid/connect/authorize"
                    assert set(query["scope"][0].split()) == {"openid", "profile", "email"}
            correlation = " ".join(headers.get_all("Set-Cookie", [])).lower()
            assert "httponly" in correlation
            assert ("secure" in correlation) is not http_mode
            assert ("samesite=lax" if http_mode else "samesite=none") in correlation
            cancellation = {"error": "access_denied", "state": query["state"][0]}
            result = request(callback, cancellation) if oidc and not http_mode and provider != "slack" else request(callback + "?" + urllib.parse.urlencode(cancellation))
            cancelled = urllib.parse.urlsplit(result[1].get("Location", ""))
            assert result[0] == 302 and cancelled.netloc == "localhost:3000" and cancelled.fragment == "providers"
            assert urllib.parse.parse_qs(cancelled.query) == {"view": ["login"], "error": [provider + "-cancelled"]}
            for state in (query["state"][0], "alterado"):
                replay = urllib.parse.urlencode({"code": "no-real-code", "state": state})
                status, headers, _ = request(callback + "?" + replay)
                failure = urllib.parse.urlsplit(headers.get("Location", ""))
                assert status == 302 and failure.netloc in ("", "localhost:3000")
                assert urllib.parse.parse_qs(failure.query).get("error") == [provider + "-failed"]
                assert ".FinanzasPersonales.Auth=" not in headers.get("Set-Cookie", "")
            assert json.loads(request("/api/auth/me")[2])["isAuthenticated"] is False

        # Alias antiguos Dev3, mismos endpoints y cookie compartida.
        for provider in ("dropbox", "microsoft"):
            assert request(f"/auth/{provider}/login")[0] == 302
            assert request(f"/auth/{provider}/user")[0] == 401
        status, headers, _ = request("/auth/dev3/logout", {"__RequestVerificationToken": token})
        assert status == 302 and headers["Location"] == "/"
        assert ".FinanzasPersonales.Auth=" in " ".join(headers.get_all("Set-Cookie", []))
        status, _, document = request("/openapi/v1.json")
        assert status == 200
        paths = json.loads(document)["paths"]
        for provider in PROVIDERS:
            assert all(verb in paths[f"/api/auth/{provider}/login"] for verb in ("get", "post"))
            assert "get" in paths[f"/api/auth/{provider}/user"]
        assert request("/scalar/v1")[0] == 200
        print("OK " + ("HTTP" if http_mode else "HTTPS") + ": ocho proveedores backend, seis botones; " +
              ("challenge/cancelación/state/PKCE y retorno al frontend con fixtures." if configured else "sin credenciales."))
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        reader.join(timeout=5)
        keys.cleanup()
        assert not any("fixture-not-a-real-secret" in line for line in logs), "Se filtró un secreto de prueba."


def main():
    verify(False)
    verify(True)
    verify(True, http_mode=True)
    print("No se efectuó un login interactivo real con ningún proveedor.")


if __name__ == "__main__":
    main()
