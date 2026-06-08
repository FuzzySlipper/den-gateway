# Den Channels `all_messages_except_self` loop guard

> **Historical archive:** `den-gateway` is decommissioned. This document is retained for fact-finding or porting old behavior to active owners (Core, Channels, den-host, Hermes Bridge, FleetOps/den-network). Do not treat it as a current implementation plan or deployment runbook.

Task: #1521

## Operator note

`all_messages_except_self` is intended for broad human-message wake-up in channels where every non-self human message should reach the agent. It is not a safe standing policy for multi-agent peer chatter unless Gateway explicitly bounds the cascade.

Gateway now treats agent-authored Den Channels messages, especially visible replies with `sourceKind=gateway_delivery` and `messageKind=agent_text`, as cascade events for this policy. Those messages do **not** recursively fan out to peer agents through `all_messages_except_self`.

Safe default behavior:

- human/user `human_text` messages can wake `all_messages_except_self` members other than the sender;
- a member's own visible reply is still self-suppressed;
- agent-authored `gateway_delivery` interim/final replies are suppressed for peer-agent fan-out;
- direct-agent and explicit mention policies remain the preferred way to intentionally route agent-to-agent work.

If a product need emerges for agent-to-agent fan-out, add an explicit/direct-agent event with bounded metadata instead of relying on ambient `all_messages_except_self` channel scrollback.

## Regression shape

The QuillForge failure was two active agents in one project channel, both on `all_messages_except_self`. A human message correctly woke both once, then visible agent replies (`sourceKind=gateway_delivery`) from one agent were treated as non-self messages for the other. Interim plus final replies doubled the pulse and produced alternating acknowledgements.

The regression tests cover:

1. a human message wakes both active agents once;
2. peer `gateway_delivery` interim/final replies create no delivery requests;
3. self-suppression still prevents waking the authoring member;
4. created channel delivery requests carry `cascade_depth` metadata for future policy guards.
