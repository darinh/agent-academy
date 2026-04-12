#!/usr/bin/env node
// check-spec-drift.js — Analyzes changed files for spec drift.
// Reads file list from stdin (one per line), drift map from specs/drift-map.json.

const fs = require('fs');
const path = require('path');

const repoRoot = path.resolve(__dirname, '..');
const driftMapPath = path.join(repoRoot, 'specs', 'drift-map.json');

let driftMap;
try {
  driftMap = JSON.parse(fs.readFileSync(driftMapPath, 'utf8'));
} catch (err) {
  console.log(`::warning::Failed to load specs/drift-map.json: ${err.message}`);
  process.exit(0);
}

const input = fs.readFileSync('/dev/stdin', 'utf8');
const changedFiles = input.split('\n').filter(Boolean);

if (changedFiles.length === 0) {
  console.log('✅ No file changes detected.');
  process.exit(0);
}

function matchesExclude(file, pattern) {
  // Convert glob pattern to regex for reliable matching
  const re = new RegExp('^' + pattern
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*\*/g, '\0')
    .replace(/\*/g, '[^/]*')
    .replace(/\0/g, '.*')
    // Trailing / means "anything inside this directory"
    .replace(/\/$/, '/.*')
  );
  return re.test(file);
}

function isExcluded(file) {
  return driftMap.excludes.some(p => matchesExclude(file, p));
}

function matchesPattern(file, pattern) {
  if (pattern.includes('*')) {
    const re = new RegExp('^' + pattern
      .replace(/[.+^${}()|[\]\\]/g, '\\$&')
      .replace(/\*\*/g, '\0')
      .replace(/\*/g, '[^/]*')
      .replace(/\0/g, '.*')
    );
    return re.test(file);
  }
  if (pattern.endsWith('/')) return file.startsWith(pattern);
  // Exact file match (no trailing slash, no wildcards)
  return file === pattern;
}

// Identify changed spec sections
const changedSpecs = new Set();
changedFiles.forEach(f => {
  const m = f.match(/^specs\/(\d{3})-/);
  if (m) changedSpecs.add(m[1]);
});

// Map source files to expected specs
const expectedSpecs = new Set();
const unmappedFiles = [];
let mappedCount = 0;

changedFiles.forEach(file => {
  if (isExcluded(file)) return;
  if (file.startsWith('specs/')) return;

  mappedCount++;
  let matched = false;

  driftMap.mappings.forEach(({ pattern, specs }) => {
    if (matchesPattern(file, pattern)) {
      specs.forEach(s => expectedSpecs.add(s));
      matched = true;
    }
  });

  if (!matched) unmappedFiles.push(file);
});

// Find missing spec updates
const missingSpecs = [];
let specsDirEntries;
try {
  specsDirEntries = fs.readdirSync(path.join(repoRoot, 'specs'));
} catch {
  specsDirEntries = [];
}
expectedSpecs.forEach(specId => {
  const dirs = specsDirEntries.filter(d => d.startsWith(specId + '-'));
  const name = dirs[0] || specId;
  if (!changedSpecs.has(specId)) {
    missingSpecs.push({ id: specId, dir: name });
  }
});

// Report
console.log('');
console.log(`📊 Results: ${mappedCount} source file(s) checked, ${expectedSpecs.size} spec section(s) expected`);
console.log(`   Specs changed in PR: ${changedSpecs.size ? [...changedSpecs].join(' ') : '(none)'}`);
console.log(`   Specs expected from code changes: ${expectedSpecs.size ? [...expectedSpecs].join(' ') : '(none)'}`);

if (unmappedFiles.length > 0) {
  console.log('');
  console.log('⚠️  Unmapped source files (no spec mapping in drift-map.json):');
  unmappedFiles.forEach(f => console.log(`  ${f}`));
  if (process.env.GITHUB_ACTIONS) {
    console.log('::warning title=Unmapped source files::Some changed files have no spec mapping in specs/drift-map.json. Consider adding mappings.');
  }
}

if (missingSpecs.length > 0) {
  console.log('');
  console.log('⚠️  Possible spec drift — code changed but these specs were not updated:');
  missingSpecs.forEach(s => console.log(`  - specs/${s.dir}/spec.md`));
  console.log('');
  console.log('   If this is intentional, add "spec-exempt: <reason>" to the PR description or a commit message.');
  if (process.env.GITHUB_ACTIONS) {
    console.log('::warning title=Spec drift detected::Code changes may need corresponding spec updates. See job output for details.');
  }
} else {
  console.log('');
  console.log('✅ No spec drift detected.');
}

console.log('');
