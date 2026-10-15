import pathlib, plistlib, sys
source, destination, mode = sys.argv[1:]
c = plistlib.loads(pathlib.Path(source).read_bytes())
prefix = 'Reims Tahoe 25.4.0 '
c['Kernel']['Patch'] = [p for p in c['Kernel']['Patch'] if not p.get('Comment', '').startswith(prefix)]
if mode == 'Enable':
    # Keep CPU virtualization for boot; exchange only the two userspace sysctl names.
    pairs = [('compatibility counter', 'hibernatehidready\0hibernatecount\0', 'hibernatehidready\0hv_vmm_present\0'),
             ('virtual-machine query', 'boot session UUID\0hv_vmm_present\0', 'boot session UUID\0hibernatecount\0')]
    for label, find, replace in pairs:
        assert len(find) == len(replace)
        c['Kernel']['Patch'].append(dict(Arch='x86_64', Base='', Comment=prefix + label,
            Count=1, Enabled=True, Find=find.encode(), Identifier='kernel', Limit=0, Mask=b'',
            MinKernel='25.4.0', MaxKernel='25.4.0', Replace=replace.encode(), ReplaceMask=b'', Skip=0))
elif mode != 'Disable':
    raise ValueError('Unknown Apple Account compatibility mode')
pathlib.Path(destination).write_bytes(plistlib.dumps(c, sort_keys=False))
