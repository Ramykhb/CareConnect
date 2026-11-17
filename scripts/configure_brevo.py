#!/usr/bin/env python3
"""Configure real Brevo SMTP delivery for both CareConnect apps without printing secrets."""
import getpass
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys


def main():
    if not shutil.which("dotnet"):
        raise ValueError("Install the .NET SDK before configuring email.")
    print("Brevo SMTP setup for both apps")
    print("Find your SMTP login and SMTP key at https://app.brevo.com/settings/keys/smtp")
    print("Use an SMTP key, not a Brevo API key. The key will not be shown as you type.")
    login = input("Brevo SMTP login: ").strip()
    sender = input("Verified sender email address: ").strip()
    key = getpass.getpass("Brevo SMTP key: ").strip()
    if not login or not key:
        raise ValueError("SMTP login and SMTP key are required.")
    if key.lower().startswith("xkeysib-"):
        raise ValueError("That is a Brevo API key. Generate an SMTP key on the SMTP tab instead.")
    if not re.fullmatch(r"[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+", sender):
        raise ValueError("Enter your verified sender email address, without a display name.")
    settings = {"Email": {
        "Provider": "Brevo", "Host": "smtp-relay.brevo.com", "Port": 587,
        "EnableSsl": True, "UserName": login, "Password": key,
        "From": sender, "FromName": "CareConnect", "InboxUrl": ""
    }}
    root = Path(__file__).resolve().parent.parent
    for app in ("PetSitting", "BabySitting"):
        # JSON on stdin keeps the key out of process arguments and shell history.
        result = subprocess.run(["dotnet", "user-secrets", "set", "--project", str(root / "src" / app)],
                                input=json.dumps(settings), text=True, capture_output=True)
        if result.returncode:
            raise ValueError(f"Could not save {app} user secrets. Check that dotnet user-secrets is available.")
        print(f"Configured {app} for Brevo SMTP.")
    print("Restart both apps. Register an account or resend its confirmation to check delivery in your real inbox.")
    print("Production deployments must configure the same Email settings through their deployment secret store.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, EOFError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        print("\nSetup cancelled.", file=sys.stderr)
        sys.exit(130)
