# Branding

- `opossum-source.png`: the original artwork (keep it; everything else is generated from it).
- `app.ico`: the desktop app icon (copied to `src/OG_opossum_command.Desktop/Assets/app.ico`).
- `hub-icon-512.png`: Power Apps, Settings, App icon, for the Opossum Hub.
- `teams-color-192.png`: the colour icon, only if the Hub is ever submitted as a custom Teams app.
- `site-logo-64.png`: a SharePoint site logo, if you make a site.

To regenerate after changing the artwork:

    dotnet run --project tools/IconBuilder -- "<full path to source png>" "<output folder>" [cropX cropY cropSize]

Every size uses the full artwork. The three optional numbers are only for experiments: they swap in a close-up for the 16 to 32 px sizes.
The artwork contains Microsoft 365 and OneDrive logos. Fine on your own PC and inside your own work
tenant. Those logos are Microsoft's, not mine, so replace that part before any public or commercial
distribution of the icon (ADR-056).
