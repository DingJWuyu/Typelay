"""Unused Ctrl permits retire through the real GTK/Fcitx protocol boundary."""

import json
from contextlib import ExitStack

from native_e2e import plan, snapshot
from support.xtest import prepare_key, prepare_pointer
from wire import now_ms


def check_ordered_retirement(
    client, capture, state, initial, nested, dimensions, generation, output, stage
):
    from gtk_session_reentry import click_point, validated_geometry, wait_for

    evidence = {"status": "not-run", "publications": []}
    report = output / f"gtk-{stage}.json"
    configuration = {
        "enabled": False,
        "sessionEnabled": True,
        "generation": generation,
        "accept": "Ctrl+F7",
        "exit": "Ctrl+F8",
        "wake": {},
        "direct": {},
    }
    client.call("configure-shortcuts", configuration)

    def publication(source, previous=None):
        expected = snapshot(
            source, client.hello, validated_geometry(source, client.hello["backend"])
        )
        expected["target"]["backend"] = "linux-" + client.hello["backend"]
        write = plan(expected, "Replacement body.")
        if previous:
            write["sessionId"] = previous["permit"]["plan"]["sessionId"]
        return {
            "target": source["target"],
            "authority": source["authority"],
            "generation": generation,
            "lease": {
                "source": expected,
                "escapeAllowed": True,
                "commandGeneration": generation,
                "sessionRevision": 1,
            },
            "permit": {"plan": write, "observedAtMs": now_ms(), "expiresAtMs": now_ms() + 100},
        }

    def publish(value):
        item = {
            "planId": value["permit"]["plan"]["id"],
            "expiresAtMs": value["permit"]["expiresAtMs"],
            "sentAtMs": now_ms(),
        }
        evidence["publications"].append(item)
        item["reply"] = client.call("publish", value)
        item["repliedAtMs"] = now_ms()
        return item["reply"]

    with ExitStack() as resources:
        control, function, tab = [
            prepare_key(resources, name) for name in ("Control_L", "F7", "Tab")
        ]
        pointer = prepare_pointer(resources, nested)
        try:
            source = wait_for(capture, "configured Ctrl source unavailable")
            before = state()
            original = publication(source)
            assert publish(original) == {"published": True}
            control(True)
            held = wait_for(
                lambda: (value := state())
                and value["controlKeys"]["down"] == before["controlKeys"]["down"] + 1
                and value,
                "original Ctrl did not reach the GTK widget",
            )
            assert held["controlKeys"]["up"] == before["controlKeys"]["up"]
            # Capture the real post-prefix IME observation while Ctrl remains held.
            refreshed = publication(
                wait_for(capture, "held Ctrl source capture unavailable"), original
            )
            if stage == "ordered-change":
                # A valid new body is not equivalent to the original Ctrl grant.
                refreshed["permit"]["plan"].update(
                    replacement="Changed body.",
                    resultText="Changed body.",
                    caret=len("Changed body."),
                )
            publish(refreshed)
            evidence["held"] = held
            assert now_ms() < original["permit"]["expiresAtMs"], (
                "refresh missed original permit lifetime"
            )
            if stage == "ordered-refresh":
                function(True)
                function(False)
                control(False)

                def accepted_original():
                    client.call("describe")
                    return [
                        event["data"] for event in client.events if event.get("event") == "accepted"
                    ]

                receipts = wait_for(
                    accepted_original, "equivalent refresh lost original Ctrl grant"
                )
                assert (
                    len(receipts) == 1 and receipts[0]["planId"] == original["permit"]["plan"]["id"]
                )
                assert receipts[0]["receipt"]["status"] == "Verified"
                final = wait_for(
                    lambda: (value := state()) and value["first"] == "Replacement body." and value,
                    "original Ctrl did not write its exact candidate",
                )
                assert final["second"] == initial["second"]
                assert final["firstEntry"]["insertions"] == initial["firstEntry"]["insertions"] + 1
                assert final["firstEntry"]["deletions"] == initial["firstEntry"]["deletions"] + 1
                assert (
                    final["firstEntry"]["start"]
                    == final["firstEntry"]["end"]
                    == len("Replacement body.")
                )

                def refreshed_retired():
                    client.call("describe")
                    return [
                        event["data"]
                        for event in client.events
                        if event.get("event") == "writeRetired"
                    ]

                retired = wait_for(refreshed_retired, "unused refresh did not retire", timeout=2)
                assert retired == [
                    {
                        "planId": refreshed["permit"]["plan"]["id"],
                        "target": source["target"],
                        "generation": generation,
                    }
                ]
                assert evidence["publications"][1]["reply"] == {"published": True}
                evidence.update(status="passed", accepted=receipts, retired=retired, final=final)
                return
            second = held["secondEntry"]
            if stage == "ordered-retirement":
                point = click_point(
                    held,
                    (second["x"] + second["width"] / 2, second["y"] + second["height"] / 2),
                    dimensions,
                )
                pointer(*point, timing=evidence.setdefault("changeTarget", {}))
                wait_for(lambda: state().get("focus") == "fixture-second", "target change missing")
            function(True)
            function(False)
            control(False)
            refused = wait_for(
                lambda: (value := state())
                and value["functionKeys"][6]["up"] == before["functionKeys"][6]["up"] + 1
                and value["controlKeys"]["up"] == before["controlKeys"]["up"] + 1
                and value,
                "changed-target Ctrl+F7 did not pass through",
            )
            assert refused["first"] == initial["first"] and refused["second"] == initial["second"]
            for field in ("firstEntry", "secondEntry"):
                for count in ("insertions", "deletions"):
                    assert refused[field][count] == initial[field][count]
            evidence["refused"] = refused
            ids = [value["permit"]["plan"]["id"] for value in (original, refreshed)]

            def retired():
                client.call("describe")  # Drain source-thread events through the real protocol.
                items = [
                    event["data"]
                    for event in client.events
                    if event.get("event") == "writeRetired" and event["data"].get("planId") in ids
                ]
                evidence["retired"] = items
                return items if len(items) >= 2 else None

            wait_for(
                retired,
                "both unused Ctrl publications must retire before unknown settlement",
                timeout=2,
            )
            assert sorted(evidence["retired"], key=lambda item: item["planId"]) == sorted(
                [
                    {"planId": id_, "target": source["target"], "generation": generation}
                    for id_ in ids
                ],
                key=lambda item: item["planId"],
            )
            assert evidence["publications"][1]["reply"] == {"published": True}
            assert not any(event.get("event") == "accepted" for event in client.events)
            # A new independent Tab can still accept on the original source.
            client.call("configure-shortcuts", {**configuration, "accept": "Tab", "exit": "Esc"})
            if stage == "ordered-retirement":
                first = refused["firstEntry"]
                pointer(
                    *click_point(
                        refused,
                        (first["x"] + first["width"] / 2, first["y"] + first["height"] / 2),
                        dimensions,
                    ),
                    timing=evidence.setdefault("returnTarget", {}),
                )
                wait_for(
                    lambda: state().get("focus") == "fixture-first" and state().get("imeClear"),
                    "original source did not regain clear IME focus",
                )
            final = publication(wait_for(capture, "independent source capture unavailable"))
            assert publish(final) == {"published": True}
            tab(True)
            tab(False)

            def accepted():
                client.call("describe")
                return [
                    event["data"] for event in client.events if event.get("event") == "accepted"
                ]

            receipts = wait_for(accepted, "new independent Tab did not complete")
            assert len(receipts) == 1 and receipts[0]["planId"] == final["permit"]["plan"]["id"]
            assert receipts[0]["receipt"]["status"] == "Verified"
            result = wait_for(
                lambda: (value := state()) and value["first"] == "Replacement body." and value,
                "new independent Tab did not replace the original field",
            )
            assert result["second"] == initial["second"]
            assert result["firstEntry"]["insertions"] == initial["firstEntry"]["insertions"] + 1
            assert result["firstEntry"]["deletions"] == initial["firstEntry"]["deletions"] + 1
            assert (
                result["firstEntry"]["start"]
                == result["firstEntry"]["end"]
                == len("Replacement body.")
            )
            evidence.update(status="passed", accepted=receipts, final=result)
        except BaseException as error:
            evidence.update(status="failed", error=str(error))
            raise
        finally:
            function(False)
            control(False)
            tab(False)
            evidence["events"] = client.events
            evidence["finishedAtMs"] = now_ms()
            report.write_text(json.dumps(evidence, indent=2) + "\n")
