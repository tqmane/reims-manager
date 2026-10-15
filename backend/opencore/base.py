import copy, pathlib, plistlib, sys, uuid
source, target = map(pathlib.Path, sys.argv[1:])
c = plistlib.loads(source.read_bytes())
for section, keys in {'ACPI': ('Add','Delete','Patch'), 'Booter': ('MmioWhitelist','Patch'),
                      'Kernel': ('Add','Block','Force','Patch'), 'Misc': ('Entries','Tools')}.items():
    for key in keys:
        c[section][key] = []
c['DeviceProperties']['Add'] = {}
c['DeviceProperties']['Delete'] = {}
c['Booter']['Quirks'].update(EnableWriteUnprotector=False, RebuildAppleMemoryMap=True, SyncRuntimePermissions=True)
c['Kernel']['Emulate']['DummyPowerManagement'] = True
c['Kernel']['Quirks'].update(AppleXcpmExtraMsrs=True, DisableLinkeditJettison=False, ForceSecureBootScheme=False)
c['Misc']['Boot'].update(HideAuxiliary=False, Timeout=0)
c['Misc']['Debug'].update(AppleDebug=True, ApplePanic=True, DisplayLevel=0x80000042, Target=75)
c['Misc']['Serial']['Init'] = True
c['Misc']['Security'].update(ScanPolicy=0, SecureBootModel='x86legacy', DmgLoading='Signed', Vault='Optional')
apple = '7C436110-AB2A-4BBB-A880-FE41995C9F82'
c['NVRAM']['Add'][apple] = {'boot-args': '-v keepsyms=1 serial=3 debug=0x108', 'csr-active-config': bytes(4),
                          'prev-lang:kbd': b'en:252', 'run-efi-updater': 'No'}
c['NVRAM']['Delete'][apple] = ['boot-args']
generic = c['PlatformInfo']['Generic']
generic.update(SystemProductName='iMac20,1', SystemUUID=str(uuid.uuid4()).upper(),
               ROM=b'\x02\x52\x45\x49\x4d\x53')
driver = next(x for x in c['UEFI']['Drivers'] if x['Path'] == 'OpenRuntime.efi')
hfs = copy.deepcopy(driver)
hfs['Path'], hfs['Comment'] = 'OpenHfsPlus.efi', 'OpenCore HFS+ driver'
partition = copy.deepcopy(driver)
partition['Path'], partition['Comment'] = 'OpenPartitionDxe.efi', 'Apple partition map support'
c['UEFI']['Drivers'] = [driver, partition, hfs]
c['UEFI']['Output']['Resolution'] = '1920x1080'
target.write_bytes(plistlib.dumps(c, sort_keys=False))
