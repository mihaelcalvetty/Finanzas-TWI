"""Compila el harness contra la API existente, sin otro proyecto ni claves/secretos reales."""
from pathlib import Path
import os
import subprocess

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = Path(os.environ.get("FINANZAS_TEST_OUTPUT", str(ROOT / "src/FinanzasPersonales.Api/bin/Debug/net10.0")))


def main():
    sdks = subprocess.check_output(["dotnet", "--list-sdks"], text=True).splitlines()
    version, location = [line for line in sdks if line.startswith("10.")][-1].split(" ", 1)
    dotnet_root = Path(location.strip("[]")).parent
    assert (OUTPUT / "FinanzasPersonales.Api.dll").exists(), "Compilar primero la API."
    artifact = OUTPUT / "AuthSessionVerification.dll"
    assert not artifact.exists(), "Revisar el artefacto anterior antes de sobrescribirlo."
    refs = []
    for name in ("Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref"):
        versions = list((dotnet_root / "packs" / name).glob("10.*/ref/net10.0"))
        newest = max(versions, key=lambda p: tuple(map(int, p.parents[1].name.split("."))))
        refs.extend(newest.glob("*.dll"))
    refs.extend(OUTPUT.glob("*.dll"))
    # Igual que el SDK/MSBuild: referencias NuGet ya resueltas en bin/ (1701/1702).
    command = ["dotnet", str(dotnet_root / "sdk" / version / "Roslyn/bincore/csc.dll"),
               "-nologo", "-nullable:enable", "-nowarn:1701,1702", "-target:exe", f"-out:{artifact}"]
    command.extend(f"-r:{path}" for path in refs)
    command.append(str(ROOT / "tests/AuthSessionVerification.cs"))
    try:
        subprocess.run(command, check=True)
        subprocess.run(["dotnet", "exec", "--runtimeconfig", str(OUTPUT / "FinanzasPersonales.Api.runtimeconfig.json"),
                        "--depsfile", str(OUTPUT / "FinanzasPersonales.Api.deps.json"), str(artifact), str(ROOT)],
                       check=True, timeout=60)
    finally:
        if artifact.exists():
            artifact.unlink()


if __name__ == "__main__":
    main()
