#!/usr/bin/env python3
"""Owner-only device enrollment over SSH stdin. Never exposes server private keys.

Mutations run in a detached worker and are polled by job ID: restarting Xray
can interrupt the very SSH connection used to request a change.
"""
import copy
import fcntl
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

CONFIG = Path('/usr/local/etc/xray/config.json')
JOBS = Path('/var/lib/worktunnel/devices')
XRAY = '/usr/local/bin/xray'
PREFIX = 'wt-device:'


def guid(value):
    return str(uuid.UUID(str(value)))


def inbound(config):
    matches = [i for i in config.get('inbounds', []) if i.get('protocol') == 'vless'
               and i.get('streamSettings', {}).get('security') == 'reality']
    if len(matches) != 1:
        raise ValueError('Expected exactly one VLESS REALITY inbound')
    return matches[0]


def edit(config, request):
    result = copy.deepcopy(config)
    clients = inbound(result)['settings']['clients']
    identity = guid(request['id'])
    if request['operation'] == 'add':
        label = request['name'].strip()
        if not 1 <= len(label) <= 60 or any(ord(c) < 32 for c in label):
            raise ValueError('Device name must be 1–60 printable characters')
        existing = [c for c in clients if c['id'] == identity]
        if existing:
            if existing[0].get('email') != PREFIX + label:
                raise ValueError('Device identity is already in use')
            return result, False
        if any(c.get('email', '').casefold() == (PREFIX + label).casefold() for c in clients):
            raise ValueError('A device with that name already exists')
        clients.append({'id': identity, 'email': PREFIX + label, 'flow': 'xtls-rprx-vision'})
    elif request['operation'] == 'revoke':
        if identity == guid(request['requester']):
            raise ValueError('Cannot revoke this PC from itself')
        matches = [c for c in clients if c['id'] == identity]
        if not matches:
            return result, False
        if not matches[0].get('email', '').startswith(PREFIX):
            raise ValueError('Existing legacy clients are protected; only enrolled devices can be revoked here')
        clients.remove(matches[0])
    else:
        raise ValueError('Unknown operation')
    return result, True


def atomic_json(path, value):
    fd, name = tempfile.mkstemp(dir=path.parent, prefix='.device-')
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump(value, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def run(*args):
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)


def apply_config(config, backup):
    # Same-directory atomic replacement, retaining the existing owner/mode/xattrs.
    fd, name = tempfile.mkstemp(dir=CONFIG.parent, suffix='.json')
    os.close(fd)
    try:
        shutil.copy2(CONFIG, name)
        stat = CONFIG.stat()
        os.chown(name, stat.st_uid, stat.st_gid)
        with open(name, 'w') as stream:
            json.dump(config, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        run(XRAY, 'run', '-test', '-c', name)
        shutil.copy2(CONFIG, backup)
        os.chmod(backup, 0o600)
        os.replace(name, CONFIG)
        try:
            run('systemctl', 'restart', 'xray')
            time.sleep(1)
            run('systemctl', 'is-active', '--quiet', 'xray')
        except Exception:
            # Restore the original content without adopting the backup's restrictive mode.
            fd, recovery = tempfile.mkstemp(dir=CONFIG.parent, suffix='.json')
            os.close(fd)
            shutil.copy2(CONFIG, recovery)
            os.chown(recovery, stat.st_uid, stat.st_gid)
            with open(recovery, 'wb') as stream:
                stream.write(backup.read_bytes())
            os.replace(recovery, CONFIG)
            run('systemctl', 'restart', 'xray')
            raise RuntimeError('Xray restart failed; original configuration restored')
    finally:
        if os.path.exists(name):
            os.unlink(name)


def worker(job_id):
    path = JOBS / (guid(job_id) + '.json')
    with open(JOBS / 'lock', 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        job = json.loads(path.read_text())
        if job['state'] != 'pending':
            return
        try:
            config = json.loads(CONFIG.read_text())
            updated, changed = edit(config, job['request'])
            if changed:
                apply_config(updated, JOBS / (job_id + '.backup.json'))
            job['state'] = 'done'
            job['message'] = 'Device added' if job['request']['operation'] == 'add' else 'Device revoked'
        except Exception as error:
            job['state'] = 'failed'
            job['message'] = str(error) if isinstance(error, (ValueError, RuntimeError)) else 'Server operation failed; inspect server logs'
        atomic_json(path, job)


def handle(request):
    action = request.get('action')
    if action == 'list':
        config = inbound(json.loads(CONFIG.read_text()))
        reality = config['streamSettings']['realitySettings']
        return {'devices': [{'id': c['id'], 'name': c.get('email', 'Existing client').removeprefix(PREFIX),
                             'managed': c.get('email', '').startswith(PREFIX)} for c in config['settings']['clients']],
                'sni': reality['serverNames'][0], 'shortId': reality['shortIds'][0], 'port': config['port']}
    if action == 'status':
        job = json.loads((JOBS / (guid(request['job']) + '.json')).read_text())
        return {k: job[k] for k in ('state', 'message')}
    if action == 'submit':
        job_id = guid(request['job'])
        path = JOBS / (job_id + '.json')
        # Validate against current data before starting a worker, then validate again under lock.
        edit(json.loads(CONFIG.read_text()), request)
        with open(JOBS / 'submission-lock', 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            if path.exists():
                if json.loads(path.read_text())['request'] != request:
                    raise ValueError('Job ID already used for a different request')
            else:
                atomic_json(path, {'state': 'pending', 'message': 'Applying device change', 'request': request})
                subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--worker', job_id],
                                 stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL, start_new_session=True, close_fds=True)
        return {'state': 'pending', 'message': 'Device change accepted'}
    raise ValueError('Unknown action')


def main():
    if os.geteuid() != 0:
        raise SystemExit('Run with sudo')
    JOBS.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(JOBS, 0o700)
    if len(sys.argv) == 3 and sys.argv[1] == '--worker':
        worker(guid(sys.argv[2]))
        return
    try:
        request = json.loads(sys.stdin.read(16384))
        print(json.dumps(handle(request)))
    except Exception as error:
        print(json.dumps({'error': str(error) if isinstance(error, (ValueError, FileNotFoundError)) else 'Device operation failed'}))
        sys.exit(1)


if __name__ == '__main__':
    main()
