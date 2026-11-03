"""Locate this service tree from the file that imported it."""

from __future__ import annotations

import pathlib


def discover_root() -> pathlib.Path:
    here = pathlib.Path(__file__).resolve().parent.parent
    marker = here / "config" / "service-catalog.json"
    if marker.is_file():
        return here
    raise SystemExit(f"Ratio service root not found beside {here}")


def discover_drive() -> pathlib.Path:
    # <drive>/ModuleServicesData/Ratio -> <drive>, where Models/ lives.
    return discover_root().parent.parent
