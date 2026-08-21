// Scenario k6 du benchmark comparatif (voir benchmark/README.md) : meme sequence exacte que
// benchmark/scenarios/tempest-checkout.yaml (login puis checkout, meme panier), meme rampe
// 20 -> 150 iterations/s sur 90s. executor "ramping-arrival-rate" = le modele ouvert de k6,
// l'equivalent direct de --from-rps/--to-rps de Tempest.
//
// preAllocatedVUs/maxVUs sont volontairement genereux (60/200) : la saturation qu'on veut
// observer vient du ConcurrencyGate de Tempest.SampleTarget, pas du pool de VUs de k6 lui-meme.
// Si k6 doit quand meme droper des iterations (dropped_iterations), c'est un signal a part —
// documente separement dans RESULTS.md, pas melange a la latence rapportee.

import http from 'k6/http';
import { check } from 'k6';

export const options = {
  // p50/p95/p99 explicites : le resume par defaut de k6 n'inclut pas p99, or c'est la colonne
  // que benchmark/normalize compare directement a p99Milliseconds de Tempest.
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(50)', 'p(95)', 'p(99)'],
  scenarios: {
    checkout: {
      executor: 'ramping-arrival-rate',
      startRate: 20,
      timeUnit: '1s',
      preAllocatedVUs: 60,
      maxVUs: 200,
      stages: [
        { target: 150, duration: '90s' },
      ],
    },
  },
};

const BASE_URL = __ENV.TARGET_URL || 'http://localhost:5281';

export default function () {
  const loginRes = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ username: 'demo', password: 'demo' }),
    { headers: { 'Content-Type': 'application/json' } },
  );

  check(loginRes, { 'login 200': (r) => r.status === 200 });
  if (loginRes.status !== 200) {
    return;
  }

  const token = loginRes.json('token');

  const checkoutRes = http.post(
    `${BASE_URL}/api/checkout`,
    JSON.stringify({ items: [{ productId: 1, quantity: 2 }] }),
    {
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
    },
  );

  check(checkoutRes, { 'checkout 200': (r) => r.status === 200 });
}
