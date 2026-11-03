#!/usr/bin/env python3
"""Collect non-secret runtime state for the local Codex model aggregator."""

import json
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone


sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from service_root import discover_root

ROOT = discover_root()
CATALOG_PATH = ROOT / 'config/service-catalog.json'
STATUS_PATH = ROOT / 'status/runtime-status.json'
PREFIX_CACHE_ROOT = ROOT / 'prefix-cache'
PREFIX_CACHE_CONFIG = PREFIX_CACHE_ROOT / 'config.json'
PREFIX_CACHE_DB = PREFIX_CACHE_ROOT / 'index.sqlite3'


def utc_now():
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace('+00:00', 'Z')


def http_probe(url, timeout=5):
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'RatioService/0.1'})
        with urllib.request.urlopen(req, timeout=timeout) as response:
            body = response.read(512).decode('utf-8', errors='replace')
            return {'state': 'up', 'http_status': response.status, 'body_prefix': body}
    except urllib.error.HTTPError as exc:
        return {'state': 'http_error', 'http_status': exc.code}
    except (urllib.error.URLError, TimeoutError, socket.timeout) as exc:
        return {'state': 'down', 'error': str(exc)}
    except Exception as exc:
        return {'state': 'error', 'error': f'{type(exc).__name__}: {exc}'}


def unit_state(unit):
    try:
        result = subprocess.run(
            ['systemctl', '--user', 'is-active', unit],
            capture_output=True,
            text=True,
            timeout=5,
        )
        state = result.stdout.strip() or result.stderr.strip()
        return {'state': state or 'unknown', 'returncode': result.returncode}
    except Exception as exc:
        return {'state': 'unknown', 'error': f'{type(exc).__name__}: {exc}'}


def docker_state(container):
    docker = shutil.which('docker')
    if not docker:
        return {'state': 'unavailable'}
    try:
        result = subprocess.run(
            [docker, 'inspect', '--format', '{{.State.Status}}|{{.State.Health.Status}}', container],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            return {'state': 'missing', 'error': result.stderr.strip()}
        status, _, health = result.stdout.strip().partition('|')
        return {'state': status or 'unknown', 'health': health or None}
    except Exception as exc:
        return {'state': 'unknown', 'error': f'{type(exc).__name__}: {exc}'}


def file_info(path):
    p = pathlib.Path(path)
    try:
        stat = p.stat()
        return {'exists': True, 'bytes': stat.st_size, 'path': str(p)}
    except FileNotFoundError:
        return {'exists': False, 'path': str(p)}
    except Exception as exc:
        return {'exists': False, 'path': str(p), 'error': str(exc)}


def download_info(state_path):
    p = pathlib.Path(state_path)
    try:
        data = json.loads(p.read_text())
        total = int(data.get('chunks_total') or 0)
        if not total and data.get('total_bytes') and data.get('chunk_bytes'):
            total = (int(data['total_bytes']) + int(data['chunk_bytes']) - 1) // int(data['chunk_bytes'])
        completed = len(data.get('completed') or [])
        return {
            'state_file': str(p),
            'total_bytes': data.get('total_bytes'),
            'chunk_bytes': data.get('chunk_bytes'),
            'completed_chunks': completed,
            'total_chunks': total,
            'computed_progress_percent': round(completed / total * 100, 2) if total else None,
            'MiB_per_second': data.get('MiB_per_second')
        }
    except FileNotFoundError:
        return {'state': 'missing', 'state_file': str(p)}
    except Exception as exc:
        return {'state': 'error', 'state_file': str(p), 'error': str(exc)}


def prefix_cache_info():
    result = {'root': str(PREFIX_CACHE_ROOT), 'config': str(PREFIX_CACHE_CONFIG)}
    try:
        config = json.loads(PREFIX_CACHE_CONFIG.read_text())
        result.update({
            'max_bytes': config.get('max_bytes'),
            'policy': (config.get('eviction') or {}).get('policy'),
            'reserve_free_bytes': config.get('reserve_free_bytes'),
        })
    except FileNotFoundError:
        result['state'] = 'not_initialized'
    except Exception as exc:
        result['state'] = 'config_error'
        result['error'] = str(exc)
    try:
        import sqlite3
        with sqlite3.connect(PREFIX_CACHE_DB) as conn:
            entries, used, hits = conn.execute(
                'SELECT COUNT(*), COALESCE(SUM(payload_bytes), 0), COALESCE(SUM(hits), 0) FROM entries'
            ).fetchone()
        result.update({'entries': entries, 'used_bytes': used, 'hits': hits})
    except FileNotFoundError:
        result.update({'entries': 0, 'used_bytes': 0, 'hits': 0})
    except Exception as exc:
        result['index_error'] = str(exc)
    try:
        usage = shutil.disk_usage(PREFIX_CACHE_ROOT)
        result['filesystem_free_bytes'] = usage.free
    except Exception as exc:
        result['filesystem_error'] = str(exc)
    return result


def main():
    catalog = json.loads(CATALOG_PATH.read_text())
    providers = []
    for provider in catalog.get('providers', []):
        item = {'id': provider['id'], 'kind': provider.get('kind'), 'display_name': provider.get('display_name')}
        health = provider.get('health') or {}
        if health.get('url') and not health.get('auth_required'):
            item['health'] = http_probe(health['url'])
        elif health.get('auth_required'):
            item['health'] = {'state': 'needs_auth_probe'}
        discovery = provider.get('model_discovery') or {}
        if discovery.get('url') and not discovery.get('auth_ref'):
            item['model_discovery'] = http_probe(discovery['url'])
        elif discovery.get('url'):
            item['model_discovery'] = {'state': 'needs_auth_probe', 'auth_ref': discovery.get('auth_ref')}
        runtime = provider.get('runtime') or {}
        if runtime.get('systemd_user_unit'):
            item['runtime'] = unit_state(runtime['systemd_user_unit'])
        if runtime.get('docker_service'):
            item['runtime'] = docker_state(runtime['docker_service'])
        providers.append(item)

    models = []
    for model in catalog.get('models', []):
        item = {
            'id': model['id'],
            'display_name': model.get('display_name'),
            'provider_id': model.get('provider_id'),
            'declared_status': model.get('status'),
        }
        for key in ('model_path', 'partial_path', 'model_blob'):
            if model.get(key):
                item[key] = file_info(model[key])
        if model.get('download_state'):
            item['download'] = download_info(model['download_state'])
        if model.get('download_state_dir'):
            item['download_dir'] = file_info(model['download_state_dir'])
        models.append(item)

    status = {
        'schema_version': 1,
        'collected_at': utc_now(),
        'catalog': str(CATALOG_PATH),
        'providers': providers,
        'models': models,
        'storage': {
            'models_root': file_info(catalog['storage']['models_root']),
            'runtime_root': file_info(catalog['storage']['runtime_root']),
            'aggregator_root': file_info(catalog['storage']['aggregator_root']),
            'prefix_cache': prefix_cache_info(),
        },
    }
    STATUS_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = STATUS_PATH.with_suffix('.tmp')
    tmp.write_text(json.dumps(status, indent=2, ensure_ascii=False) + '\n')
    os.replace(tmp, STATUS_PATH)
    print(json.dumps(status, indent=2, ensure_ascii=False))


if __name__ == '__main__':
    main()
