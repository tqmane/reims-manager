"""Apply the Reims filesystem scan profile without changing other boot choices."""
import pathlib
import plistlib
import sys

source, destination = map(pathlib.Path, sys.argv[1:])
config = plistlib.loads(source.read_bytes())
config['Misc']['Security']['ScanPolicy'] = 0x301
destination.write_bytes(plistlib.dumps(config, sort_keys=False))
