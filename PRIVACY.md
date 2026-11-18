# Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the
user.

## What stays on your computer

- Sensor readings, usage history, game sessions and benchmark results are stored locally in
  `C:\ProgramData\Nodekeeper` (service) and `%AppData%\Nodekeeper` (dashboard).
- Nodekeeper has no telemetry, analytics, accounts, ads or crash reporting. The dashboard and the service do not
  contact any server on the internet.
- The only exception is the installer: if the PawnIO sensor driver is missing and you leave **Install the PawnIO
  driver** checked, it downloads the official PawnIO installer from GitHub
  (`github.com/namazso/PawnIO.Setup/releases`). That is a plain file download; nothing about you or your PC is sent.

## What is shared on your local network, and only when you ask

- The Nodekeeper service listens on TCP port 47821 (HTTPS), opened in Windows Firewall for **private and domain** networks only (not public networks).
- It answers only dashboards you have paired yourself, by accepting an access request on that PC or by typing a
  one-time 6-digit code. Each paired dashboard gets its own token; you can remove a machine from the dashboard and
  revoke a dashboard on the service (`Nodekeeper.Service.exe revogar`).
- The dashboard's network speed test sends random data between your own two PCs.

## Third parties

Nodekeeper does not use third-party services. The bundled open-source libraries
([third-party notices](THIRD-PARTY-NOTICES.md)) run locally and do not send data anywhere.

## Changes

Any future feature that sends data off your network (for example an opt-in benchmark ranking) will be off by
default, explained in the app before it is enabled, and described here.
