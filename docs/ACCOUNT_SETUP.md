# Microsoft account setup for Sakana Launcher

Sakana uses Microsoft's device-code sign-in flow. Players do not need to create an Entra app registration or enter a Client ID; Sakana manages its login application internally.

Launching Minecraft requires a Microsoft account that owns Minecraft: Java Edition and passes the Minecraft profile check. Sakana does not provide offline or cracked launching.

## Sign in

1. Open **Sakana Launcher → 账户**.
2. Select **登录 Microsoft 账户**.
3. Open the Microsoft device-code page, enter the code shown by Sakana, and approve the sign-in.
4. Return to Sakana and select **我已完成授权**.

Sakana refreshes the account session automatically. The refresh credential is protected for the current Windows user with DPAPI. Signing out removes it. Access tokens remain in memory and are redacted from captured game output.

## If Minecraft Services rejects the launcher

Microsoft sign-in can succeed while Minecraft Services rejects the launcher at the Minecraft Java account step. New launcher integrations may need review through the [official Minecraft Java Edition Game Service API process](https://help.minecraft.net/hc/en-us/articles/16254801392141). This is an application authorization issue, not a setting players can fix in Sakana.

Never share passwords, device codes, access tokens, refresh tokens, or Minecraft bearer tokens.
