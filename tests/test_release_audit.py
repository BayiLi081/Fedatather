import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('release_audit', Path(__file__).resolve().parents[1] / 'tools' / 'prepare_public_release.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class PublicReleaseTests(unittest.TestCase):
    def test_private_and_unreviewed_files_rejected(self):
        for name in ['.git/config', '.aws/credentials', 'assets/boss_here.jpg', 'docs/private.pdf', 'tools/token.txt']:
            self.assertFalse(audit.allowed(name), name)
        for name in ['README.md', 'assets/boss_public.jpg', 'docs/screenshots/phone-tabs.png', 'src/Program.cs']:
            self.assertTrue(audit.allowed(name), name)

    def test_credentials_paths_and_metadata_rejected(self):
        original = audit.ROOT
        with tempfile.TemporaryDirectory() as folder:
            audit.ROOT = Path(folder)
            try:
                (audit.ROOT / 'src').mkdir()
                (audit.ROOT / 'assets').mkdir()
                cases = [b'ghp_' + b'A' * 36, b'AKIA' + b'Z' * 16,
                         b'-----BEGIN ' + b'PRIVATE KEY-----', b'C:' + bytes([92]) + b'Users' + bytes([92]) + b'sample-person',
                         b'/home/' + b'sample-person/private']
                for sample in cases:
                    (audit.ROOT / 'src' / 'Example.cs').write_bytes(sample)
                    self.assertTrue(audit.audit(['src/Example.cs']))
                (audit.ROOT / 'assets' / 'boss_public.jpg').write_bytes(b'Exif' + bytes([0, 0]))
                self.assertTrue(audit.audit(['assets/boss_public.jpg']))
                (audit.ROOT / 'src' / 'Example.cs').write_text('namespace Demo {}')
                self.assertEqual(audit.audit(['src/Example.cs']), [])
            finally:
                audit.ROOT = original


if __name__ == '__main__':
    unittest.main()
