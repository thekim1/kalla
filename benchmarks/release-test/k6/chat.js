// The R1 release test's chat load (repo-and-deployment §8.3): streamed chat messages at a constant arrival rate.
// Adapted from the gateway's benchmarks/compare/k6/chat.js: one target (the chat API's /v1/chat/completions, or
// FakeGateway's in a dry run) instead of three, an arrival rate instead of a number of clients, and the request from a
// file of ../requests/ shared with stream_probe.py. k6 reads a response only once it has ended, so it measures the
// whole stream; time to first token comes from stream_probe.py, which run.sh starts next to this script.
//
//   BASE_URL  e.g. https://rag-api.example.se (without /v1)     MODEL     assistant slug (FakeGateway model in a dry run)
//   REQUEST   path of a request file (mounted at /requests)     KEY       bearer key (optional)
//   HEADERS   JSON object of extra headers                      RATE      messages per second    DURATION  e.g. 10m
//   OUT       results file, e.g. /results/phase1.k6.json        VUS       pre-allocated clients [60]
//
// Every response is checked: status 200, answer text in the stream, no error event, and the closing data: [DONE], so
// a target that fails fast cannot look faster than it is.
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const spec = JSON.parse(open(__ENV.REQUEST));
const MODEL = __ENV.MODEL;
const RATE = Number(__ENV.RATE || 4);
const DURATION = __ENV.DURATION || '10m';
const URL = `${(__ENV.BASE_URL || '').replace(/\/+$/, '').replace(/\/v1$/, '')}/v1/chat/completions`;
const EXTRA = JSON.parse(__ENV.HEADERS || '{}');
const failedStreams = new Counter('failed_streams');

export const options = {
  scenarios: {
    chat: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      // About rate × stream length; k6 adds clients up to maxVUs and counts dropped_iterations past that.
      preAllocatedVUs: Number(__ENV.VUS || 60),
      maxVUs: Number(__ENV.VUS || 60) * 4,
      gracefulStop: '60s',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  discardResponseBodies: false,
};

function fill(value, question) {
  if (typeof value === 'string') {
    let out = value.replaceAll('{model}', MODEL).replaceAll('{question}', question);
    while (out.includes('{uuid}')) out = out.replace('{uuid}', crypto.randomUUID());
    return out;
  }
  if (Array.isArray(value)) return value.map((v) => fill(v, question));
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, fill(v, question)]));
  }
  return value;
}

export default function () {
  const question = spec.questions[(__VU * 7919 + __ITER) % spec.questions.length];
  const headers = { 'Content-Type': 'application/json', ...EXTRA, ...fill(spec.headers || {}, question) };
  if (__ENV.KEY) headers.Authorization = `Bearer ${__ENV.KEY}`;
  const res = http.post(URL, JSON.stringify(fill(spec.body, question)), { headers, timeout: '60s' });
  const ok = check(res, {
    'status 200': (r) => r.status === 200,
    'answer text': (r) => typeof r.body === 'string' && /"content":"[^"]/.test(r.body),
    'no error event': (r) => typeof r.body === 'string' && !/^data: *\{"error"/m.test(r.body),
    'stream complete': (r) => typeof r.body === 'string' && r.body.includes('data: [DONE]'),
  });
  if (!ok) failedStreams.add(1);
}

export function handleSummary(data) {
  const values = (name) => (data.metrics[name] ? data.metrics[name].values : null);
  const count = (name) => (data.metrics[name] ? data.metrics[name].values.count : 0);
  const result = {
    model: MODEL,
    request: (__ENV.REQUEST || '').split('/').pop().replace(/\.json$/, ''),
    rate: RATE,
    duration: DURATION,
    requests: count('http_reqs'),
    rps: data.metrics.http_reqs ? data.metrics.http_reqs.values.rate : 0,
    failedStreams: count('failed_streams'),
    checksFailed: data.metrics.checks ? data.metrics.checks.values.fails : 0,
    droppedIterations: count('dropped_iterations'),
    // http_req_duration ends with the stream; http_req_waiting is the time to the response headers.
    whole_ms: values('http_req_duration'),
    headers_ms: values('http_req_waiting'),
  };
  const whole = result.whole_ms || { med: 0, 'p(95)': 0 };
  return {
    [__ENV.OUT]: JSON.stringify(result, null, 2),
    stdout: `k6: ${result.requests} streams, ${result.rps.toFixed(2)}/s, failed ${result.failedStreams}, dropped ${result.droppedIterations}, whole stream median ${whole.med.toFixed(0)} ms, p95 ${whole['p(95)'].toFixed(0)} ms\n`,
  };
}
