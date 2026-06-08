# den-gateway agent guidance

`den-gateway` is decommissioned and historical-only. Do not implement new features, deploy, restart, or route active Den work here.

Use this repository only for historical fact-finding or to port old behavior to the active owner:

- `den-core` for workflow truth, worker runs/assignments/leases, and reconciliation.
- `den-channels` for channel messages, memberships/subscriptions, direct-agent events, and activity/current-work projections.
- `den-host` for machine-local runtime host and process/run evidence.
- `den-hermes-bridge` for Hermes-specific profile/config/session glue.
- `den-network` / FleetOps for infrastructure and fleet operations.

See Den document `_global/gateway-decommission-routing-policy`.
