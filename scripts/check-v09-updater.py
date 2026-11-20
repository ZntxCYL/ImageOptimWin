#!/usr/bin/env python3
"""Offline source guards for 0.9.0 updater and existing 0.8.1 fixes."""
from pathlib import Path
from xml.etree import ElementTree as ET
import json
import re
import sys

root = Path(__file__).resolve().parent.parent
errors = []

def check(rel, words):
    path = root / rel
    if not path.is_file():
        errors.append(f'File missing: {rel}')
        return
    value = path.read_text(encoding='utf-8')
    for text in words:
        if text not in value:
            errors.append(f'{rel}: missing {text!r}')

check('src/ImageOptim.Win/ImageOptim.Win.csproj', [
    'ExcludeFromSingleFile="true"', 'update-config.json'])
check('src/ImageOptim.Win/MainWindow.xaml', ['CheckUpdates_Click', 'Window_PreviewDrop', 'ThemeSelectedText'])
check('src/ImageOptim.Win/MainWindow.xaml.cs', ['CheckUpdatesAsync', 'ConfirmHealthyStartup', 'existing.ResetForRetry();'])
check('src/ImageOptim.Win/Services/UpdateService.cs', [
    'RSA.Create()', 'VerifyData(manifestBytes, signatureBytes', 'HashAlgorithmName.SHA256',
    'CryptographicException', 'IncrementalHash', 'StartUpdater', 'ConfirmHealthyStartup',
    'https://github.com/', 'releases/latest', 'ImageOptim.Updater.exe'])
check('src/ImageOptim.Win/Services/UpdateService.cs', [
    'EnsureGitHubResponseOk', 'Uri.UriSchemeHttps', 'HashAlgorithmName.SHA256',
    'response.RequestMessage?.RequestUri', 'releases/download/{tag}/'])
if 'api.github.com/repos/' in (root / 'src/ImageOptim.Win/Services/UpdateService.cs').read_text(encoding='utf-8'):
    errors.append('Latest release lookup must not use unauthenticated GitHub REST API')
check('src/ImageOptim.Win/MainWindow.xaml', [
    'HeaderStyle="{StaticResource RightAlignedColumnHeader}"',
    'x:Name="AgainButton"', 'x:Name="StopButton"', 'x:Name="ClearRecordsButton"'])
check('src/ImageOptim.Win/MainWindow.xaml.cs', [
    'AgainButton.Visibility = settled ? Visibility.Visible : Visibility.Collapsed',
    'StopButton.Visibility = _isRunning ? Visibility.Visible : Visibility.Collapsed'])
check('src/ImageOptim.Win/PreferencesWindow.xaml', [
    '<TabItem Header="关于">', 'x:Name="VersionText"'])
check('src/ImageOptim.Win/PreferencesWindow.xaml.cs', [
    'typeof(PreferencesWindow).Assembly.GetName().Version',
    'VersionText.Text = "当前版本：v"'])
check('src/ImageOptim.Win/UpdateWindow.xaml.cs', ['DownloadAsync', 'StartUpdater', 'Cancel_Click'])
check('src/ImageOptim.Updater/Program.cs', ['WaitForExitAsync', 'Directory.Move(install, backup)',
    'Directory.Move(stage, install)', 'Directory.Move(backup, install)', 'health-',
    'SHA256.HashDataAsync', 'ExtractSafely', 'TryStartOldVersion', 'ImageOptim.Win.exe'])
check('build-linux.sh', ['check-v081-build.py', 'check-v09-updater.py',
    'ImageOptim.Updater.csproj', 'ImageOptim.Updater.exe', 'update-config.json',
    'svgo/node_modules/svgo/bin/svgo'])
check('publish.ps1', ['ImageOptim.Updater.csproj', 'ImageOptim.Updater.exe',
    'ExcludeFromSingleFile', 'svgo\\bin\\svgo'])
check('publish.ps1', ['ControlTemplate TargetType="{x:Type DataGridCell}"',
    'running status icon must not use infinite rotation'])
if 'RenderTransformOrigin="0.5,0.5"' in (root / 'publish.ps1').read_text(encoding='utf-8'):
    errors.append('publish.ps1 retains the obsolete animated-icon guard')
check('scripts/release-github.sh', ['gh release create', 'make-update-release.py', '--dry-run'])
check('scripts/build-and-release.sh', ['build-linux.sh', 'release-github.sh'])
check('scripts/make-update-release.py', ['openssl', 'dgst', '-sign', 'update-manifest.json',
    'update-manifest.sig', 'sha256', 'private-key'])
check('scripts/verify-update-release.py', ['openssl', 'sha256', 'update-config.json', 'RSA-SHA256'])
check('.github/workflows/release-windows.yml', [
    'workflow_dispatch:', 'tags:', 'IMAGEOPTIM_UPDATE_PRIVATE_KEY',
    'Verify downloaded release assets', 'contents: write', '--verify-tag',
    'actions/upload-artifact@v6', 'actions/download-artifact@v7'])
check('src/ImageOptim.Win/Services/ThemeManager.cs', ['using System.IO;'])
check('src/ImageOptim.Win/Services/OptimizerService.cs', ['"bin", "svgo"'])
# Accept future patch/minor releases without weakening compatibility checks.
try:
    versions = []
    for rel in ('src/ImageOptim.Win/ImageOptim.Win.csproj', 'src/ImageOptim.Updater/ImageOptim.Updater.csproj'):
        doc = ET.parse(root / rel).getroot()
        raw = next((e.text for e in doc.iter() if e.tag == 'Version'), None)
        match = re.fullmatch(r'(\d+)\.(\d+)\.(\d+)', raw or '')
        if not match:
            errors.append(f'{rel}: missing semantic Version')
            continue
        value = tuple(map(int, match.groups()))
        if value < (0, 9, 0):
            errors.append(f'{rel}: updater project must be v0.9.0 or later')
        versions.append(value)
    if len(versions) == 2 and versions[0] != versions[1]:
        errors.append('Application and updater versions disagree')
except Exception as e:
    errors.append('Version check failed: ' + str(e))
try:
    cfg = json.loads((root / 'src/ImageOptim.Win/update-config.json').read_text(encoding='utf-8'))
    if set(cfg) != {'repository', 'publicKeyPem'}: errors.append('Malformed update-config.json schema')
    elif cfg['repository'] != 'ZntxCYL/ImageOptimWin' or not cfg['publicKeyPem'].startswith('-----BEGIN PUBLIC KEY-----'):
        errors.append('Release update repository/public key is not configured')
except Exception as e:
    errors.append('Invalid update-config.json: ' + str(e))
for rel in ['src/ImageOptim.Win/ImageOptim.Win.csproj', 'src/ImageOptim.Updater/ImageOptim.Updater.csproj',
            'src/ImageOptim.Win/MainWindow.xaml', 'src/ImageOptim.Win/UpdateWindow.xaml']:
    try: ET.parse(root / rel)
    except Exception as e: errors.append(f'XML parse error {rel}: {e}')

if errors:
    for error in errors: print('[FAIL]', error)
    sys.exit(1)
print('[PASS] v0.9 updater source/config regression checks')
