"""Validate the toolkit or browser's visible parent-local anchor without inventing Wayland screen coordinates."""

import math


def anchor_geometry(native: dict, space: str = "gtk-toplevel") -> dict | None:
    """Core's RectPx uses physical pixels in this target's native parent space.

    Only the in-process renderer may place this anchor. It cannot be mixed with
    compositor/output rectangles or used to position a separate desktop window.
    """
    try:
        if (
            space not in ("gtk-toplevel", "gtk4-toplevel", "browser-viewport")
            or native["space"] != space
        ):
            return None
        scale = native["scale"]
        width, height = native["frameWidth"], native["frameHeight"]
        bounds = native["bounds"]
        x, y, w, h = (bounds[key] for key in ("x", "y", "width", "height"))
        if not all(
            type(v) in (int, float) and math.isfinite(v) for v in (scale, width, height, x, y, w, h)
        ):
            return None
        minimum_scale = 0.1 if space == "browser-viewport" else 1
        if not minimum_scale <= scale <= 8 or width <= 0 or height <= 0:
            return None
        if x < 0 or y < 0 or w <= 0 or h <= 0 or x + w > width or y + h > height:
            return None
        return {"bounds": {key: bounds[key] * scale for key in ("x", "y", "width", "height")}}
    except (KeyError, TypeError):
        return None
