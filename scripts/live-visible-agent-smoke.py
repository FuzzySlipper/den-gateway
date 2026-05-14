#!/usr/bin/env python3
"""Run a live synthetic visible-agent smoke through Core -> Gateway -> Hermes-style claim/ack.

The script intentionally uses only HTTP contracts. It does not read sibling service databases
and only writes marked synthetic Den Core ops entries plus Gateway-owned delivery/binding state.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


def utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def iso(value: dt.datetime) -> str:
    return value.isoformat().replace("+00:00", "Z")


def request_json(method: str, url: str, payload: object | None = None, timeout: float = 8.0) -> object:
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            body = response.read().decode("utf-8")
            if not body:
                return {"status_code": response.status}
            return json.loads(body)
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", "replace")
        raise RuntimeError(f"{method} {url} failed with HTTP {error.code}: {body}") from error
    except urllib.error.URLError as error:
        raise RuntimeError(f"{method} {url} failed: {error}") from error


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def as_dict(value: object, label: str) -> dict:
    require(isinstance(value, dict), f"{label} was not a JSON object: {value!r}")
    return value


def print_step(name: str, evidence: object) -> None:
    print(f"\n## {name}")
    print(json.dumps(evidence, indent=2, sort_keys=True))


def main() -> int:
    parser = argparse.ArgumentParser(description="Live Den Gateway visible-agent integration smoke")
    parser.add_argument("--gateway-url", default=os.environ.get("GATEWAY_URL", "http://127.0.0.1:5300"))
    parser.add_argument("--core-url", default=os.environ.get("DEN_CORE_URL", "http://192.168.1.10:18080/den-core-api"))
    parser.add_argument("--channels-url", default=os.environ.get("DEN_CHANNELS_URL", "http://192.168.1.10:18080"))
    parser.add_argument("--project-id", default=os.environ.get("DEN_GATEWAY_SMOKE_PROJECT", "den-gateway"))
    parser.add_argument("--target-agent", default=os.environ.get("DEN_GATEWAY_SMOKE_AGENT", "den-gateway-runner"))
    parser.add_argument("--target-role", default=os.environ.get("DEN_GATEWAY_SMOKE_ROLE", "runner"))
    parser.add_argument("--adapter-instance-id", default=os.environ.get("DEN_GATEWAY_SMOKE_ADAPTER", "den-gateway-smoke-local"))
    parser.add_argument("--dedupe-key", default=os.environ.get("DEN_GATEWAY_SMOKE_DEDUPE"))
    parser.add_argument("--claim-attempts", type=int, default=5)
    args = parser.parse_args()

    gateway_url = args.gateway_url.rstrip("/")
    core_url = args.core_url.rstrip("/")
    channels_url = args.channels_url.rstrip("/")
    run_id = uuid.uuid4().hex[:12]
    dedupe_key = args.dedupe_key or f"den-gateway-visible-agent-smoke:{args.project_id}:{args.target_agent}:{run_id}"
    observed_at = utc_now()

    evidence: dict[str, object] = {"dedupe_key": dedupe_key, "run_id": run_id}

    ready = as_dict(request_json("GET", f"{gateway_url}/health/ready"), "Gateway readiness")
    require(ready.get("status") == "ready", "Gateway readiness was not ready")
    evidence["gateway_ready"] = ready
    print_step("gateway readiness", ready)

    core_ready = as_dict(request_json("GET", f"{core_url}/api/gateway/readiness"), "Core readiness")
    require(core_ready.get("status") in {"ready", "degraded"}, "Den Core gateway readiness was not ready/degraded")
    evidence["core_ready"] = {"status": core_ready.get("status"), "service": core_ready.get("service")}
    print_step("core readiness", evidence["core_ready"])

    channels_ready = as_dict(request_json("GET", f"{channels_url}/api/gateway/health"), "Channels health")
    require(channels_ready.get("status") == "ready", "Den Channels gateway health was not ready")
    evidence["channels_ready"] = channels_ready
    print_step("channels health", channels_ready)

    heartbeat = as_dict(request_json("PUT", f"{gateway_url}/api/adapter-bindings/heartbeat", {
        "adapter_kind": "hermes_profile",
        "adapter_instance_id": args.adapter_instance_id,
        "agent_identity": args.target_agent,
        "project_id": args.project_id,
        "role": args.target_role,
        "status": "active",
        "capabilities_json": json.dumps({"delivery_modes": ["wake", "notify"], "synthetic": True}),
        "metadata_json": json.dumps({"synthetic": True, "smoke_run_id": run_id}),
        "last_seen_at": iso(observed_at),
        "expires_at": iso(observed_at + dt.timedelta(minutes=10)),
    }), "Gateway binding heartbeat")
    require(int(heartbeat.get("binding_id", 0)) > 0, "Binding heartbeat did not return a binding_id")
    evidence["binding_heartbeat"] = heartbeat
    print_step("gateway binding heartbeat", heartbeat)

    sentinel_event = as_dict(request_json("POST", f"{core_url}/api/gateway/sentinel/events", {
        "sentinel_id": "den-gateway-visible-agent-smoke",
        "event_type": "visible_agent_smoke_probe",
        "state": "normal",
        "project_id": args.project_id,
        "outage_id": None,
        "reason": "synthetic visible-agent smoke probe",
        "observed_at": iso(observed_at),
        "cursor": None,
        "metadata": {
            "synthetic": "true",
            "smoke_run_id": run_id,
            "targetIdentity": args.target_agent,
            "targetType": "agent",
            "deliveryMode": "wake",
            "reason": "visible_agent_smoke",
        },
        "dedupe_key": dedupe_key,
    }), "Core sentinel event")
    require(sentinel_event.get("dedupe_key") == dedupe_key, "Core did not echo the synthetic dedupe key")
    evidence["core_sentinel_event"] = sentinel_event
    print_step("core synthetic ops event", sentinel_event)

    outbox_query = urllib.parse.urlencode({"projectId": args.project_id, "limit": "50"})
    outbox = as_dict(request_json("GET", f"{core_url}/api/events/outbox?{outbox_query}"), "Core outbox")
    outbox_items = outbox.get("items") or []
    require(any(isinstance(item, dict) and item.get("dedupe_key") == dedupe_key for item in outbox_items), "Synthetic event was not visible in Core outbox")
    evidence["core_outbox"] = {"matched_dedupe_key": dedupe_key, "item_count": len(outbox_items)}
    print_step("core outbox visibility", evidence["core_outbox"])

    poll = as_dict(request_json("POST", f"{gateway_url}/api/delivery-loop/poll", {
        "source": "core",
        "project_id": args.project_id,
        "limit": 50,
        "now": iso(utc_now()),
    }), "Gateway delivery poll")
    require(poll.get("status") == "completed", f"Gateway poll did not complete: {poll}")
    evidence["gateway_poll"] = poll
    print_step("gateway ingestion poll", poll)

    claimed = None
    claim_response = None
    for attempt in range(1, args.claim_attempts + 1):
        claim_response = as_dict(request_json("POST", f"{gateway_url}/api/deliveries/claim", {
            "adapter_kind": "hermes_profile",
            "adapter_instance_id": args.adapter_instance_id,
            "project_id": args.project_id,
            "agent_identity": args.target_agent,
            "role": args.target_role,
            "accepted_delivery_modes": ["wake"],
            "limit": 10,
            "lease_seconds": 120,
            "claimed_at": iso(utc_now()),
        }), "Gateway claim")
        deliveries = claim_response.get("deliveries") or []
        claimed = next((item for item in deliveries if isinstance(item, dict) and item.get("dedupe_key") == dedupe_key), None)
        if claimed is not None:
            break
        time.sleep(0.2)
    require(claimed is not None, f"Hermes-style claim did not return synthetic delivery; last response={claim_response}")
    evidence["gateway_claim"] = claimed
    print_step("hermes-style claim", claimed)

    delivery_id = claimed["delivery_request_id"]
    attempt_id = claimed["attempt_id"]
    complete = as_dict(request_json("POST", f"{gateway_url}/api/deliveries/{delivery_id}/complete", {
        "attempt_id": attempt_id,
        "ack_kind": "synthetic_smoke_completed",
        "adapter_kind": "hermes_profile",
        "adapter_instance_id": args.adapter_instance_id,
        "external_message_id": f"synthetic-smoke-{run_id}",
        "session_id": f"smoke-{run_id}",
        "observed_at": iso(utc_now()),
        "metadata_json": json.dumps({"synthetic": True, "dedupe_key": dedupe_key}),
    }), "Gateway completion callback")
    require(complete.get("status") == "completed", f"Completion callback did not complete delivery: {complete}")
    evidence["gateway_complete"] = complete
    print_step("hermes-style ack/complete", complete)

    print("\nPASS visible-agent smoke completed")
    print(json.dumps(evidence, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001 - top-level smoke should print a concise failure.
        print(f"\nFAIL visible-agent smoke: {exc}", file=sys.stderr)
        raise SystemExit(1)
