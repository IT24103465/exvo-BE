import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '..', '..');
const testPlan = join(scriptDirectory, 'exvo-load-test.jmx');

const profiles = {
  smoke: { threads: 1, ramp: 5, duration: 35 },
  demo: { threads: 10, ramp: 10, duration: 50 },
  load: { threads: 30, ramp: 60, duration: 270 },
  stress: { threads: 100, ramp: 180, duration: 240 },
};

function argumentValue(name) {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : undefined;
}

function jmeterCommand() {
  if (process.env.JMETER_BIN) return { executable: process.env.JMETER_BIN, prefix: [] };
  if (process.env.JMETER_HOME) {
    return {
      executable: process.env.JAVA_HOME
        ? join(process.env.JAVA_HOME, 'bin', process.platform === 'win32' ? 'java.exe' : 'java')
        : 'java',
      prefix: ['-jar', join(process.env.JMETER_HOME, 'bin', 'ApacheJMeter.jar')],
    };
  }
  if (process.platform === 'win32') {
    const located = spawnSync('where.exe', ['jmeter.bat'], { encoding: 'utf8' });
    const batchFile = located.status === 0 ? located.stdout.trim().split(/\r?\n/)[0] : '';
    if (batchFile) {
      return {
        executable: process.env.JAVA_HOME ? join(process.env.JAVA_HOME, 'bin', 'java.exe') : 'java',
        prefix: ['-jar', join(dirname(batchFile), 'ApacheJMeter.jar')],
      };
    }
  }
  return { executable: process.platform === 'win32' ? 'jmeter.bat' : 'jmeter', prefix: [] };
}

function runJMeter(argumentsList) {
  const command = jmeterCommand();
  const result = spawnSync(command.executable, [...command.prefix, ...argumentsList], {
    cwd: repositoryRoot,
    env: process.env,
    shell: process.platform === 'win32' && command.prefix.length === 0,
    stdio: 'inherit',
  });

  if (result.error) {
    throw new Error(`Unable to start JMeter through ${command.executable}: ${result.error.message}`);
  }
  return result.status ?? 1;
}

function ensureSupportedJava() {
  const executable = process.env.JAVA_HOME
    ? join(process.env.JAVA_HOME, 'bin', process.platform === 'win32' ? 'java.exe' : 'java')
    : 'java';
  const result = spawnSync(executable, ['-version'], { encoding: 'utf8' });
  if (result.error) throw new Error(`Unable to inspect Java: ${result.error.message}`);
  const versionOutput = `${result.stdout || ''}\n${result.stderr || ''}`;
  const match = versionOutput.match(/version\s+"(\d+)/);
  const major = match ? Number(match[1]) : Number.NaN;
  if (!Number.isInteger(major) || major < 17 || major > 21) {
    throw new Error(
      `This JMeter plan requires Java 17-21 (Java 17 recommended); detected '${versionOutput.trim()}'. ` +
      'Set JAVA_HOME to a supported JDK before running the load test.',
    );
  }
}

ensureSupportedJava();

if (process.argv.includes('--gui')) {
  process.exit(runJMeter(['-t', testPlan]));
}

if (process.argv.includes('--validate')) {
  const validationDirectory = mkdtempSync(join(tmpdir(), 'exvo-jmeter-validation-'));
  try {
    const exitCode = runJMeter([
      '-n',
      '-t', testPlan,
      '-JsetupThreads=0',
      '-Jthreads=0',
      '-Jduration=1',
      '-l', join(validationDirectory, 'validation.jtl'),
      '-j', join(validationDirectory, 'jmeter.log'),
    ]);
    if (exitCode !== 0) process.exit(exitCode);
    console.log('JMeter loaded the JMX successfully without sending traffic.');
  } finally {
    rmSync(validationDirectory, { recursive: true, force: true });
  }
  process.exit(0);
}

const profileName = argumentValue('--profile') || process.env.PROFILE || 'smoke';
const profile = profiles[profileName];
if (!profile) {
  throw new Error(`Unknown profile '${profileName}'. Use smoke, demo, load, or stress.`);
}

const rawBaseUrl = process.env.BASE_URL || 'http://localhost:5000';
let target;
try {
  target = new URL(rawBaseUrl);
} catch {
  throw new Error(`BASE_URL is not a valid absolute URL: ${rawBaseUrl}`);
}
if (!['http:', 'https:'].includes(target.protocol)) {
  throw new Error('BASE_URL must use http:// or https://.');
}

const timestamp = new Date().toISOString().replaceAll(':', '-').replace(/\.\d{3}Z$/, 'Z');
const runDirectory = join(scriptDirectory, 'results', `${timestamp}-${profileName}`);
const reportDirectory = join(runDirectory, 'report');
const resultsFile = join(runDirectory, 'results.jtl');
const logFile = join(runDirectory, 'jmeter.log');
const summaryFile = join(runDirectory, 'summary.json');
mkdirSync(runDirectory, { recursive: true });

const basePath = target.pathname === '/' ? '' : target.pathname.replace(/\/$/, '');
const port = target.port || (target.protocol === 'https:' ? '443' : '80');
const jmeterArguments = [
  '-n',
  '-t', testPlan,
  `-Jprofile=${profileName}`,
  `-Jthreads=${profile.threads}`,
  `-Jramp=${profile.ramp}`,
  `-Jduration=${profile.duration}`,
  `-Jprotocol=${target.protocol.slice(0, -1)}`,
  `-Jhost=${target.hostname}`,
  `-Jport=${port}`,
  `-JbasePath=${basePath}`,
  `-JeventId=${process.env.EVENT_ID || ''}`,
  `-JauthEnabled=${Boolean(process.env.LOAD_TEST_EMAIL && process.env.LOAD_TEST_PASSWORD)}`,
  '-Jjmeter.reportgenerator.overall_granularity=2000',
  '-Jjmeter.reportgenerator.apdex_satisfied_threshold=800',
  '-Jjmeter.reportgenerator.apdex_tolerated_threshold=1500',
  '-l', resultsFile,
  '-j', logFile,
  '-e',
  '-o', reportDirectory,
];

console.log(`Running JMeter ${profileName} profile against ${target.origin}${basePath}`);
console.log(`Threads: ${profile.threads}; ramp-up: ${profile.ramp}s; duration: ${profile.duration}s`);
console.log(`Artifacts: ${runDirectory}`);

const jmeterExitCode = runJMeter(jmeterArguments);
if (jmeterExitCode !== 0) {
  console.error(`JMeter exited with code ${jmeterExitCode}. Inspect ${logFile}.`);
  process.exit(jmeterExitCode);
}

const engineLog = readFileSync(logFile, 'utf8');
if (/Uncaught Exception|Error running groovy script|\bERROR\b/.test(engineLog)) {
  console.error(`JMeter recorded an engine/script error. Inspect ${logFile}.`);
  process.exit(1);
}

const assertion = spawnSync(
  process.execPath,
  [join(scriptDirectory, 'assert-jmeter-results.mjs'), resultsFile, profileName, summaryFile],
  { cwd: repositoryRoot, stdio: 'inherit' },
);
if (assertion.error) throw assertion.error;

console.log(`HTML dashboard: ${join(reportDirectory, 'index.html')}`);
console.log(`Machine summary: ${summaryFile}`);
process.exit(assertion.status ?? 1);
