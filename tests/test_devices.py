"""Run on Linux; all paths and service commands are replaced with test doubles."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

spec = importlib.util.spec_from_file_location('devices', Path(__file__).resolve().parents[1] / 'server' / 'worktunnel-devices.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DeviceTests(unittest.TestCase):
    def setUp(self):
        self.owner = str(uuid.uuid4())
        self.identity = str(uuid.uuid4())
        self.config = {'inbounds': [{'protocol': 'vless', 'port': 443,
            'streamSettings': {'security': 'reality', 'realitySettings': {'privateKey': 'DO-NOT-RETURN', 'serverNames': ['example.com'], 'shortIds': ['abcd']}},
            'settings': {'clients': [{'id': self.owner}]}}]}
        self.request = {'operation': 'add', 'id': self.identity, 'name': 'Phone', 'requester': self.owner}

    def test_add_and_idempotency(self):
        changed, applied = module.edit(self.config, self.request)
        self.assertTrue(applied)
        self.assertEqual(len(module.inbound(changed)['settings']['clients']), 2)
        self.assertFalse(module.edit(changed, self.request)[1])
        self.assertEqual(len(module.inbound(self.config)['settings']['clients']), 1)

    def test_duplicate_name(self):
        changed, _ = module.edit(self.config, self.request)
        with self.assertRaises(ValueError):
            module.edit(changed, dict(self.request, id=str(uuid.uuid4())))

    def test_revoke_protection(self):
        with self.assertRaises(ValueError):
            module.edit(self.config, dict(self.request, operation='revoke', id=self.owner))
        with self.assertRaises(ValueError):
            module.edit(self.config, dict(self.request, operation='revoke', id=self.owner, requester=self.identity))

    def test_revoke_only_selected(self):
        changed, _ = module.edit(self.config, self.request)
        revoked, applied = module.edit(changed, dict(self.request, operation='revoke'))
        self.assertTrue(applied)
        self.assertEqual(module.inbound(revoked)['settings']['clients'], [{'id': self.owner}])
        self.assertFalse(module.edit(revoked, dict(self.request, operation='revoke'))[1])

    def test_no_private_key_in_list(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'config.json'
            config.write_text(json.dumps(self.config))
            with patch.object(module, 'CONFIG', config):
                self.assertNotIn('DO-NOT-RETURN', json.dumps(module.handle({'action': 'list'})))

    def test_validation_before_write(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'config.json'
            config.write_text(json.dumps(self.config))
            original = config.read_bytes()
            with patch.object(module, 'CONFIG', config), patch.object(module, 'run', side_effect=RuntimeError('invalid candidate')):
                with self.assertRaises(RuntimeError):
                    module.apply_config({}, Path(directory) / 'backup')
            self.assertEqual(config.read_bytes(), original)

    def test_restart_failure_restores_configuration_and_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'config.json'
            config.write_text(json.dumps(self.config))
            config.chmod(0o640)
            original = config.read_bytes()
            calls = []
            def run(*args):
                calls.append(args)
                if len(calls) == 2:
                    raise RuntimeError('restart failed')
            with patch.object(module, 'CONFIG', config), patch.object(module, 'run', side_effect=run):
                with self.assertRaises(RuntimeError):
                    module.apply_config({'changed': True}, Path(directory) / 'backup')
            self.assertEqual(config.read_bytes(), original)
            self.assertEqual(config.stat().st_mode & 0o777, 0o640)
            self.assertEqual(calls[-1], ('systemctl', 'restart', 'xray'))

    def test_reject_ambiguous_inbound(self):
        with self.assertRaises(ValueError):
            module.inbound({'inbounds': []})

if __name__ == '__main__':
    unittest.main()
