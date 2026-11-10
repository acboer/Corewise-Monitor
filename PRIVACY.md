# Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the
user.

## What stays on your computer

- Sensor readings, usage history, game sessions and benchmark results are stored locally in
  `C:\ProgramData\Corewise` (service) and `%AppData%\Corewise` (dashboard).
- Corewise has no telemetry, analytics, accounts, ads or crash reporting. It does not contact any server on the
  internet.

## What is shared on your local network, and only when you ask

- The Corewise service listens on TCP port 47821 (HTTPS), opened in Windows Firewall for **private and domain** networks only (not public networks).
- It answers only dashboards you have paired yourself, by accepting an access request on that PC or by typing a
  one-time 6-digit code. Each paired dashboard gets its own token; you can remove a machine from the dashboard and
  revoke a dashboard on the service (`Corewise.Service.exe revogar`).
- The dashboard's network speed test sends random data between your own two PCs.

## Third parties

Corewise does not use third-party services. The bundled open-source libraries
([third-party notices](THIRD-PARTY-NOTICES.md)) run locally and do not send data anywhere.

## Changes

Any future feature that sends data off your network (for example an opt-in benchmark ranking) will be off by
default, explained in the app before it is enabled, and described here.
