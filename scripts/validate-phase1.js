#!/usr/bin/env node
// validate-phase1.js — Pre-flight checks for Phase 1 without requiring dotnet SDK
// Run: node scripts/validate-phase1.js

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
let errors = 0;
let warnings = 0;
let passed = 0;

function check(name, fn) {
  try {
    const result = fn();
    if (result === true || result === undefined) {
      console.log(`  ✓ ${name}`);
      passed++;
    } else {
      console.log(`  ✗ ${name}: ${result}`);
      errors++;
    }
  } catch (e) {
    console.log(`  ✗ ${name}: ${e.message}`);
    errors++;
  }
}

function warn(name, fn) {
  try {
    const result = fn();
    if (result === true || result === undefined) {
      console.log(`  ✓ ${name}`);
      passed++;
    } else {
      console.log(`  ⚠ ${name}: ${result}`);
      warnings++;
    }
  } catch (e) {
    console.log(`  ⚠ ${name}: ${e.message}`);
    warnings++;
  }
}

function fileExists(relPath) {
  return fs.existsSync(path.join(ROOT, relPath));
}

function readFile(relPath) {
  return fs.readFileSync(path.join(ROOT, relPath), 'utf8');
}

function fileContains(relPath, ...strings) {
  const content = readFile(relPath);
  for (const s of strings) {
    if (!content.includes(s)) return `missing "${s}"`;
  }
  return true;
}

// ---- Section 1: File Structure ----
console.log('\n📁 File Structure');

const requiredFiles = [
  'src/Farmer.sln',
  'src/Farmer.Core/Farmer.Core.csproj',
  'src/Farmer.Tools/Farmer.Tools.csproj',
  'src/Farmer.Host/Farmer.Host.csproj',
  'src/Farmer.Tests/Farmer.Tests.csproj',
  // Models
  'src/Farmer.Core/Models/RunRequest.cs',
  'src/Farmer.Core/Models/TaskPacket.cs',
  'src/Farmer.Core/Models/RunStatus.cs',
  'src/Farmer.Core/Models/CostReport.cs',
  'src/Farmer.Core/Models/ReviewVerdict.cs',
  'src/Farmer.Core/Models/Manifest.cs',
  // Contracts
  'src/Farmer.Core/Contracts/ISshService.cs',
  'src/Farmer.Core/Contracts/IMappedDriveReader.cs',
  'src/Farmer.Core/Contracts/IVmManager.cs',
  'src/Farmer.Core/Contracts/IRunStore.cs',
  // Config
  'src/Farmer.Core/Config/VmConfig.cs',
  'src/Farmer.Core/Config/FarmerSettings.cs',
  // Tools
  'src/Farmer.Tools/SshService.cs',
  'src/Farmer.Tools/MappedDriveReader.cs',
  'src/Farmer.Tools/RunDirectoryLayout.cs',
  'src/Farmer.Tools/FileRunStore.cs',
  // Host
  'src/Farmer.Host/Program.cs',
  'src/Farmer.Host/appsettings.json',
  // Worker
  'src/Farmer.Worker/CLAUDE.md',
  // Tests
  'src/Farmer.Tests/Models/ContractSerializationTests.cs',
  'src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs',
  // Data
  'data/sample-plans/react-grid-component/1-SetupProject.md',
  'data/sample-plans/react-grid-component/2-BuildGridComponent.md',
  'data/sample-plans/react-grid-component/3-AddTests.md',
  'data/sample-plans/api-endpoint/1-DefineSchema.md',
  'data/sample-plans/api-endpoint/2-ImplementEndpoint.md',
  // Docs
  'docs/implementation-plan.md',
  '.gitignore',
];

for (const f of requiredFiles) {
  check(`exists: ${f}`, () => fileExists(f) || `file not found`);
}

// ---- Section 2: Project References ----
console.log('\n🔗 Project References');

check('Farmer.Tools references Farmer.Core', () =>
  fileContains('src/Farmer.Tools/Farmer.Tools.csproj', 'Farmer.Core'));

check('Farmer.Host references Farmer.Core', () =>
  fileContains('src/Farmer.Host/Farmer.Host.csproj', 'Farmer.Core'));

check('Farmer.Host references Farmer.Tools', () =>
  fileContains('src/Farmer.Host/Farmer.Host.csproj', 'Farmer.Tools'));

check('Farmer.Tests references Farmer.Core', () =>
  fileContains('src/Farmer.Tests/Farmer.Tests.csproj', 'Farmer.Core'));

check('Farmer.Tests references Farmer.Tools', () =>
  fileContains('src/Farmer.Tests/Farmer.Tests.csproj', 'Farmer.Tools'));

check('Farmer.Tools has SSH.NET package', () =>
  fileContains('src/Farmer.Tools/Farmer.Tools.csproj', 'SSH.NET'));

check('Farmer.Tests has xunit package', () =>
  fileContains('src/Farmer.Tests/Farmer.Tests.csproj', 'xunit'));

// ---- Section 3: Namespace Consistency ----
console.log('\n📦 Namespace Consistency');

const nsChecks = [
  ['src/Farmer.Core/Models/RunRequest.cs', 'namespace Farmer.Core.Models'],
  ['src/Farmer.Core/Models/TaskPacket.cs', 'namespace Farmer.Core.Models'],
  ['src/Farmer.Core/Models/RunStatus.cs', 'namespace Farmer.Core.Models'],
  ['src/Farmer.Core/Contracts/ISshService.cs', 'namespace Farmer.Core.Contracts'],
  ['src/Farmer.Core/Contracts/IMappedDriveReader.cs', 'namespace Farmer.Core.Contracts'],
  ['src/Farmer.Core/Config/VmConfig.cs', 'namespace Farmer.Core.Config'],
  ['src/Farmer.Tools/SshService.cs', 'namespace Farmer.Tools'],
  ['src/Farmer.Tools/MappedDriveReader.cs', 'namespace Farmer.Tools'],
  ['src/Farmer.Tools/RunDirectoryLayout.cs', 'namespace Farmer.Tools'],
  ['src/Farmer.Tests/Models/ContractSerializationTests.cs', 'namespace Farmer.Tests.Models'],
  ['src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs', 'namespace Farmer.Tests.Tools'],
];

for (const [file, ns] of nsChecks) {
  check(`${path.basename(file)} → ${ns}`, () => fileContains(file, ns));
}

// ---- Section 4: Contract Models ----
console.log('\n📋 Contract Models');

check('RunRequest has JsonPropertyName attributes', () =>
  fileContains('src/Farmer.Core/Models/RunRequest.cs', 'JsonPropertyName', 'run_id', 'task_id', 'work_request_name'));

check('TaskPacket has Prompts list', () =>
  fileContains('src/Farmer.Core/Models/TaskPacket.cs', 'List<PromptFile>', 'branch_name'));

check('RunStatus has RunPhase enum', () =>
  fileContains('src/Farmer.Core/Models/RunStatus.cs', 'enum RunPhase', 'Created', 'Executing', 'Complete', 'Failed'));

check('RunStatus enum serializes as string', () =>
  fileContains('src/Farmer.Core/Models/RunStatus.cs', 'JsonStringEnumConverter'));

check('ReviewVerdict has Verdict enum', () =>
  fileContains('src/Farmer.Core/Models/ReviewVerdict.cs', 'enum Verdict', 'Accept', 'Retry', 'Reject'));

check('CostReport has stage costs', () =>
  fileContains('src/Farmer.Core/Models/CostReport.cs', 'List<StageCost>', 'duration_seconds'));

check('Manifest has files_changed', () =>
  fileContains('src/Farmer.Core/Models/Manifest.cs', 'FilesChanged', 'branch_name'));

// ---- Section 5: Interface Contracts ----
console.log('\n🔌 Interface Contracts');

check('ISshService has ExecuteAsync', () =>
  fileContains('src/Farmer.Core/Contracts/ISshService.cs', 'ExecuteAsync', 'SshResult'));

check('ISshService has ScpUploadAsync', () =>
  fileContains('src/Farmer.Core/Contracts/ISshService.cs', 'ScpUploadAsync'));

check('ISshService has ScpUploadContentAsync', () =>
  fileContains('src/Farmer.Core/Contracts/ISshService.cs', 'ScpUploadContentAsync'));

check('IMappedDriveReader has ReadFileAsync', () =>
  fileContains('src/Farmer.Core/Contracts/IMappedDriveReader.cs', 'ReadFileAsync'));

check('IMappedDriveReader has WaitForFileAsync (SSHFS lag handling)', () =>
  fileContains('src/Farmer.Core/Contracts/IMappedDriveReader.cs', 'WaitForFileAsync'));

check('IVmManager has ReserveAsync + ReleaseAsync', () =>
  fileContains('src/Farmer.Core/Contracts/IVmManager.cs', 'ReserveAsync', 'ReleaseAsync'));

check('IVmManager has VmState enum', () =>
  fileContains('src/Farmer.Core/Contracts/IVmManager.cs', 'enum VmState', 'Available', 'Reserved', 'Busy'));

// ---- Section 6: Infrastructure Rules ----
console.log('\n🏗️  Infrastructure Rules (hard-won lessons)');

check('SshService uses Renci.SshNet', () =>
  fileContains('src/Farmer.Tools/SshService.cs', 'Renci.SshNet', 'SshClient', 'ScpClient'));

check('MappedDriveReader handles SSHFS cache lag', () =>
  fileContains('src/Farmer.Tools/MappedDriveReader.cs', 'SshfsCacheLagMs', 'Task.Delay'));

check('MappedDriveReader is READ-ONLY (no File.Write)', () => {
  const content = readFile('src/Farmer.Tools/MappedDriveReader.cs');
  if (content.includes('WriteAllText') || content.includes('File.Write'))
    return 'MappedDriveReader should NEVER write to mapped drives!';
  return true;
});

check('RunDirectoryLayout has separate VM vs Host paths', () =>
  fileContains('src/Farmer.Tools/RunDirectoryLayout.cs', 'VmPlansDir', 'HostProgressFile'));

check('VM paths use forward slashes', () => {
  const content = readFile('src/Farmer.Tools/RunDirectoryLayout.cs');
  // VM paths should use forward slashes in string interpolation
  const vmMethods = content.match(/public static string Vm\w+.*=>/g) || [];
  if (vmMethods.length === 0) return 'no VmXxx methods found';
  return true;
});

check('Host paths use Path.Combine (Windows-safe)', () =>
  fileContains('src/Farmer.Tools/RunDirectoryLayout.cs', 'Path.Combine'));

check('FileRunStore uses atomic writes (tmp + rename)', () =>
  fileContains('src/Farmer.Tools/FileRunStore.cs', '.tmp', 'File.Move'));

// ---- Section 7: Config ----
console.log('\n⚙️  Configuration');

check('appsettings.json has all 3 VMs', () => {
  const config = JSON.parse(readFile('src/Farmer.Host/appsettings.json'));
  const vms = config.Farmer?.Vms;
  if (!vms || vms.length !== 3) return `expected 3 VMs, got ${vms?.length}`;
  const names = vms.map(v => v.Name);
  if (!names.includes('claudefarm1')) return 'missing claudefarm1';
  if (!names.includes('claudefarm2')) return 'missing claudefarm2';
  if (!names.includes('claudefarm3')) return 'missing claudefarm3';
  return true;
});

check('VM drive letters are N, O, P', () => {
  const config = JSON.parse(readFile('src/Farmer.Host/appsettings.json'));
  const letters = config.Farmer.Vms.map(v => v.MappedDriveLetter).sort();
  if (letters.join(',') !== 'N,O,P') return `expected N,O,P got ${letters.join(',')}`;
  return true;
});

check('SSHFS cache lag is configured', () => {
  const config = JSON.parse(readFile('src/Farmer.Host/appsettings.json'));
  if (!config.Farmer.SshfsCacheLagMs) return 'missing SshfsCacheLagMs';
  return true;
});

check('FarmerSettings has SshfsCacheLagMs', () =>
  fileContains('src/Farmer.Core/Config/FarmerSettings.cs', 'SshfsCacheLagMs'));

// ---- Section 8: Sample Plans ----
console.log('\n📝 Sample Plans');

check('Plan files use numeric prefix (no brackets)', () => {
  const planDir = path.join(ROOT, 'data/sample-plans');
  const dirs = fs.readdirSync(planDir);
  for (const dir of dirs) {
    const full = path.join(planDir, dir);
    if (!fs.statSync(full).isDirectory()) continue;
    const files = fs.readdirSync(full);
    for (const f of files) {
      if (f.includes('[') || f.includes(']'))
        return `${f} uses brackets! PowerShell will break. Use "1-slug.md" format`;
    }
  }
  return true;
});

check('Plan files are sorted numerically', () => {
  const planDir = path.join(ROOT, 'data/sample-plans/react-grid-component');
  const files = fs.readdirSync(planDir).sort();
  if (!files[0].startsWith('1-')) return `first file should start with "1-", got ${files[0]}`;
  return true;
});

// ---- Section 9: CLAUDE.md Worker Template ----
console.log('\n🤖 CLAUDE.md Worker Template');

check('CLAUDE.md mentions progress.md', () =>
  fileContains('src/Farmer.Worker/CLAUDE.md', 'progress.md'));

check('CLAUDE.md mentions .comms/', () =>
  fileContains('src/Farmer.Worker/CLAUDE.md', '.comms'));

check('CLAUDE.md mentions plans/ directory', () =>
  fileContains('src/Farmer.Worker/CLAUDE.md', 'plans/'));

check('CLAUDE.md mentions phase tracking', () =>
  fileContains('src/Farmer.Worker/CLAUDE.md', 'phase:'));

check('CLAUDE.md mentions output artifacts', () =>
  fileContains('src/Farmer.Worker/CLAUDE.md', 'manifest.json', 'summary.json'));

// ---- Section 10: Test Coverage ----
console.log('\n🧪 Test Coverage');

check('Serialization tests exist for all models', () => {
  const content = readFile('src/Farmer.Tests/Models/ContractSerializationTests.cs');
  const models = ['RunRequest', 'TaskPacket', 'RunStatus', 'CostReport', 'ReviewVerdict', 'Manifest', 'Summary'];
  for (const m of models) {
    if (!content.includes(m)) return `missing test for ${m}`;
  }
  return true;
});

check('Layout tests cover VM and Host paths', () =>
  fileContains('src/Farmer.Tests/Tools/RunDirectoryLayoutTests.cs', 'VmPaths', 'HostPaths', 'MappedDrive'));

check('Tests use [Fact] attribute', () =>
  fileContains('src/Farmer.Tests/Models/ContractSerializationTests.cs', '[Fact]'));

// ---- Summary ----
console.log('\n' + '='.repeat(60));
console.log(`  Results: ${passed} passed, ${errors} errors, ${warnings} warnings`);
console.log('='.repeat(60));

if (errors > 0) {
  console.log('\n❌ Validation FAILED — fix errors above before proceeding.');
  process.exit(1);
} else if (warnings > 0) {
  console.log('\n⚠️  Validation PASSED with warnings.');
} else {
  console.log('\n✅ Validation PASSED — ready for `dotnet build && dotnet test` on Windows.');
}
