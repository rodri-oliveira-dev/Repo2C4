#!/usr/bin/env python3
"""Build the reviewed C2 model around an inspected semantic-c3 dogfood snapshot."""

import json
import sys
from pathlib import Path

if len(sys.argv) != 3:
    raise SystemExit("usage: build-semantic-c3-dogfood-model.py SNAPSHOT OUTPUT")

snapshot = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))

def evidence_id(category, path):
    matches = [
        item["id"] for item in snapshot["evidence"]
        if item["category"] == category and item["relativePath"] == path
    ]
    if len(matches) != 1:
        raise SystemExit(f"expected one {category} evidence for {path}, got {len(matches)}")
    return matches[0]

def external(kind, technology, project):
    findings = snapshot.get("externalIntegrationEvidence", {}).get("evidence", [])
    matches = [
        item["id"] for item in findings
        if item["kind"] == kind
        and item["technology"] == technology
        and item["projectPath"] == project
    ]
    if len(matches) != 1:
        raise SystemExit(
            f"expected one external {kind}/{technology} finding for {project}, got {len(matches)}"
        )
    return matches[0]

projects = {
    "ingestion_api": "src/Ingestion.Api/Ingestion.Api.csproj",
    "ingestion_persistence": "src/Ingestion.Persistence/Ingestion.Persistence.csproj",
    "outbox": "src/Ingestion.Outbox.Worker/Ingestion.Outbox.Worker.csproj",
    "consolidation_api": "src/Consolidation.Api/Consolidation.Api.csproj",
    "consolidation_persistence": "src/Consolidation.Persistence/Consolidation.Persistence.csproj",
    "consolidation_worker": "src/Consolidation.Worker/Consolidation.Worker.csproj",
}
project_evidence = {
    key: evidence_id("dotnet.project", value) for key, value in projects.items()
}

ingestion_db = external("database", "postgresql", projects["ingestion_persistence"])
redis = external("cache", "redis", projects["ingestion_api"])
rabbit_publish = external("messaging", "rabbitmq", projects["outbox"])
consolidation_db = external("database", "postgresql", projects["consolidation_persistence"])
rabbit_consume = external("messaging", "rabbitmq", projects["consolidation_worker"])

def container(identifier, name, evidence_ids):
    return {
        "id": identifier,
        "kind": "container",
        "name": name,
        "parentId": "el_lab",
        "evidenceIds": evidence_ids,
        "status": "requiresReview",
        "reviewReason": "Dogfood C2 boundary is reviewed separately from static Semantic C3 discovery.",
    }

def external_element(identifier, name, evidence_ids):
    return {
        "id": identifier,
        "kind": "softwareSystem",
        "name": name,
        "evidenceIds": evidence_ids,
        "status": "requiresReview",
        "reviewReason": "External resource identity is normalized from static integration evidence.",
    }

def relation(identifier, source, destination, description, evidence_id_value):
    return {
        "id": identifier,
        "sourceId": source,
        "destinationId": destination,
        "description": description,
        "evidenceIds": [evidence_id_value],
        "status": "requiresReview",
        "reviewReason": "Dogfood runtime relation remains reviewable.",
    }

model = {
    "schemaVersion": "1.0",
    "level": "C2",
    "snapshot": snapshot,
    "elements": [
        {
            "id": "el_lab",
            "kind": "softwareSystem",
            "name": "dotnet-observability-lab",
            "evidenceIds": [],
            "status": "requiresReview",
            "reviewReason": "Fixture system scope mirrors the documented dogfood repository.",
        },
        container("el_ingestion_api", "Ingestion.Api",
                  [project_evidence["ingestion_api"], project_evidence["ingestion_persistence"]]),
        container("el_ingestion_outbox_worker", "Ingestion.Outbox.Worker",
                  [project_evidence["outbox"], project_evidence["ingestion_persistence"]]),
        container("el_consolidation_api", "Consolidation.Api",
                  [project_evidence["consolidation_api"], project_evidence["consolidation_persistence"]]),
        container("el_consolidation_worker", "Consolidation.Worker",
                  [project_evidence["consolidation_worker"], project_evidence["consolidation_persistence"]]),
        external_element("el_postgresql", "PostgreSQL", [ingestion_db, consolidation_db]),
        external_element("el_redis", "Redis", [redis]),
        external_element("el_rabbitmq", "RabbitMQ", [rabbit_publish, rabbit_consume]),
    ],
    "relations": [
        relation("rel_ingestion_api_postgresql", "el_ingestion_api", "el_postgresql",
                 "Uses ingestion_db", ingestion_db),
        relation("rel_ingestion_api_redis", "el_ingestion_api", "el_redis",
                 "Uses Redis idempotency cache", redis),
        relation("rel_outbox_postgresql", "el_ingestion_outbox_worker", "el_postgresql",
                 "Claims ingestion Outbox", ingestion_db),
        relation("rel_outbox_rabbitmq", "el_ingestion_outbox_worker", "el_rabbitmq",
                 "Publishes integration events", rabbit_publish),
        relation("rel_consolidation_api_postgresql", "el_consolidation_api", "el_postgresql",
                 "Reads consolidation_db", consolidation_db),
        relation("rel_rabbitmq_consolidation_worker", "el_rabbitmq", "el_consolidation_worker",
                 "Delivers integration events", rabbit_consume),
        relation("rel_consolidation_worker_postgresql", "el_consolidation_worker", "el_postgresql",
                 "Commits Inbox and aggregate", consolidation_db),
    ],
}

Path(sys.argv[2]).write_text(json.dumps(model, indent=2) + "\n", encoding="utf-8")
