"""Controlled framed-protocol peer; output is structural, never real inference."""

import json
import sys
from pathlib import Path

package, _, data = map(Path, sys.argv[-3:])
scenario = json.loads((package / "scenario.json").read_text())
current = data / "components/current"
current.parent.mkdir(parents=True, exist_ok=True)
starts = data / "peer-starts"
launch = int(starts.read_text()) + 1 if starts.exists() else 1
starts.write_text(str(launch))
pending = None


def send(value):
    content = json.dumps(value).encode()
    sys.stdout.buffer.write(len(content).to_bytes(4, "little") + content)
    sys.stdout.buffer.flush()


def publish(generation):
    current.unlink(missing_ok=True)
    current.symlink_to(generation)


assert sys.stdin.buffer.read(1) == b"\x01"
while size := sys.stdin.buffer.read(4):
    request = json.loads(sys.stdin.buffer.read(int.from_bytes(size, "little")))
    operation = request.get("op", request.get("event"))
    if operation == "quit":
        raise SystemExit(200)
    if operation == "start":
        if launch == 2 and scenario["failureStage"] == "cold-start":
            publish("private-generation")
        result = True
    elif operation == "activateModel":
        if request["data"]["generation"] == "b" * 32:
            pending = request
            continue
        publish(request["data"]["generation"])
        result = "confirmed"
    elif operation == "cancelModelActivation":
        assert pending and pending["data"]["identity"] == request["data"]
        request, pending = pending, None
        if scenario["failureStage"] == "cancelled-candidate-retains-current":
            publish("private-generation")
        result = scenario.get("cancelOutcome", "unchanged")
    elif operation == "settings":
        result = {"saved": True}
    elif operation == "translateEditorSnapshot":
        document = request["data"]["request"]["data"]
        result = {"data": {"documentId": document["documentId"], "translation": document["text"]}}
    else:
        raise AssertionError("unknown controlled operation")
    if operation in ("start", "activateModel", "cancelModelActivation"):
        send({"event": "status", "data": scenario["ready"]})
    send({"reply": request["id"], "data": result})
