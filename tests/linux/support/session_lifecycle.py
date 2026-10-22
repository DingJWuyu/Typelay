"""Read-only owned-session exit proof shared by Linux desktop runners."""

from pathlib import Path

from desktop_session import receipts


def session_exit_confirmed(data, identities):
    """A removed receipt is not proof that its guardian finished final cleanup."""
    if receipts(data):
        return False
    for identity in identities:
        try:
            raw = Path(f"/proc/{identity['pid']}/stat").read_text()
        except FileNotFoundError:
            continue  # Only disappearance, not a failed/denied observation.
        header, separator, tail = raw.rpartition(")")
        if not separator or "(" not in header:
            raise ValueError("owned-process-stat-invalid")
        pid, fields = header.split("(", 1)[0].strip(), tail.split()
        if int(pid) != identity["pid"] or len(fields) < 20:
            raise ValueError("owned-process-stat-invalid")
        if fields[0] != "Z" and int(fields[19]) == identity["startTime"]:
            return False
    return True
