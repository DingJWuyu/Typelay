"""Controlled GitHub release boundary for concurrent fixed.py CLI processes."""

import hashlib
import json
import os
import sqlite3
import sys
import time
from pathlib import Path


def fail(status, message):
    print(f"HTTP {status}: {message}", file=sys.stderr)
    raise SystemExit(1)


def main():
    args = sys.argv[1:]
    database = sqlite3.connect(os.environ["FIXED_DATABASE"], timeout=10)
    database.row_factory = sqlite3.Row
    database.execute(
        "CREATE TABLE IF NOT EXISTS releases "
        "(id INTEGER PRIMARY KEY, tag TEXT, draft INTEGER, name TEXT, data BLOB)"
    )

    def release(row):
        assets = []
        if row["data"] is not None:
            digest = hashlib.sha256(row["data"]).hexdigest()
            if os.environ.get("FIXED_CORRUPT") and not row["draft"]:
                digest = "0" * 64
            assets = [{"id": row["id"], "name": row["name"], "digest": "sha256:" + digest}]
        return {
            "id": row["id"],
            "tag_name": row["tag"],
            "published_at": None if row["draft"] else "2026-11-17T00:00:00Z",
            "draft": bool(row["draft"]),
            "assets": assets,
        }

    def find(tag):
        row = database.execute("SELECT * FROM releases WHERE tag=?", (tag,)).fetchone()
        if row is None:
            fail(404, "Not Found")
        return row

    if args[0] == "api":
        endpoint = args[1]
        method = args[args.index("--method") + 1] if "--method" in args else "GET"
        fields = dict(
            args[i + 1].split("=", 1) for i, arg in enumerate(args) if arg in ("-f", "-F")
        )
        if os.environ.get("FIXED_DENIED"):
            fail(403, "Forbidden")
        if "/releases/tags/" in endpoint:
            row = find(endpoint.split("/tags/", 1)[1])
            if row["draft"]:
                fail(404, "Not Found")
            print(json.dumps(release(row)))
        elif endpoint.endswith("/jobs?per_page=100"):
            Path(os.environ["GITHUB_OUTPUT"]).with_name("waiting").touch()
            published = database.execute("SELECT 1 FROM releases WHERE draft=0").fetchone()
            print(
                json.dumps(
                    {
                        "jobs": [
                            {"name": "fcitx-build", "conclusion": "success" if published else None}
                        ]
                    }
                )
            )
        elif endpoint.endswith("/releases") and method == "POST":
            with database:
                database.execute(
                    "INSERT INTO releases(tag,draft) VALUES (?,1)", (fields["tag_name"],)
                )
            print(json.dumps(release(find(fields["tag_name"]))))
        elif endpoint.endswith("/releases"):
            print(json.dumps([release(row) for row in database.execute("SELECT * FROM releases")]))
        elif method == "GET":
            row = database.execute(
                "SELECT * FROM releases WHERE id=?", (int(endpoint.rsplit("/", 1)[1]),)
            ).fetchone()
            if row is None:
                fail(404, "Not Found")
            print(json.dumps(release(row)))
        elif method == "PATCH":
            if os.environ.get("FIXED_PUBLISH_DENIED"):
                fail(403, "Forbidden")
            gate = os.environ.get("FIXED_PUBLISH_GATE")
            if gate:
                gate = Path(gate)
                (gate / str(os.getpid())).touch()
                deadline = time.monotonic() + 10
                while not (gate / "go").exists():
                    if time.monotonic() >= deadline:
                        fail(408, "Test publication gate timed out")
                    time.sleep(0.01)
            with database:
                database.execute("BEGIN IMMEDIATE")
                if database.execute(
                    "SELECT id FROM releases WHERE tag=? AND draft=0", (fields["tag_name"],)
                ).fetchone():
                    print(
                        json.dumps(
                            {
                                "errors": [
                                    {
                                        "resource": "Release",
                                        "code": "already_exists",
                                        "field": "tag_name",
                                    }
                                ]
                            }
                        )
                    )
                    fail(422, "Validation Failed")
                database.execute(
                    "UPDATE releases SET tag=?,draft=0 WHERE id=?",
                    (fields["tag_name"], int(endpoint.rsplit("/", 1)[1])),
                )
            if os.environ.get("FIXED_PUBLISH_RESPONSE_LOST"):
                fail(500, "Publication response lost")
            print(json.dumps(release(find(fields["tag_name"]))))
        elif method == "DELETE":
            if os.environ.get("FIXED_DELETE_DENIED"):
                fail(403, "Draft deletion denied")
            with database:
                database.execute(
                    "DELETE FROM releases WHERE id=?", (int(endpoint.rsplit("/", 1)[1]),)
                )
        else:
            raise AssertionError(args)
    elif args[:2] == ["release", "upload"]:
        row = find(args[2])
        path = Path(args[3])
        if os.environ.get("FIXED_UPLOAD_FAILED"):
            fail(500, "Upload interrupted")
        with database:
            database.execute(
                "UPDATE releases SET name=?,data=? WHERE id=?",
                (
                    path.name,
                    b"changed upload"
                    if os.environ.get("FIXED_UPLOAD_CORRUPT")
                    else path.read_bytes(),
                    row["id"],
                ),
            )
    else:
        raise AssertionError(args)


if __name__ == "__main__":
    main()
