// Scenario k6 du benchmark comparatif (voir benchmark/README.md) : meme sequence exacte que
// benchmark/scenarios/sirocco-checkout.yaml (login puis checkout, meme panier), meme rampe
// 20 -> 150 iterations/s sur 90s. executor "ramping-arrival-rate" = le modele ouvert de k6,
// l'equivalent direct de --from-rps/--to-rps de Sirocco.
//
// preAllocatedVUs/maxVUs sont volontairement genereux par defaut (60/200) : la saturation qu'on
// veut observer dans le benchmark comparatif vient du ConcurrencyGate de Sirocco.SampleTarget, pas
// du pool de VUs de k6 lui-meme. Si k6 doit quand meme droper des iterations (dropped_iterations),
// c'est un signal a part — documente separement, pas melange a la latence rapportee.
//
// Tout le profil est parametrable par l'environnement, avec pour defaut les valeurs du benchmark
// publie : `benchmark/run.sh` n'en passe aucune et reproduit donc exactement le tir de
// results/RESULTS.md, tandis que `benchmark/saturation.sh` (experience de l'article sur la dette
// d'ordonnancement) les surcharge toutes. START_RATE == TARGET_RATE donne un debit constant, sans
// avoir a introduire un second executeur ici.

import http from 'k6/http';
import { check } from 'k6';

const startRate = parseInt(__ENV.START_RATE || '20', 10);
const targetRate = parseInt(__ENV.TARGET_RATE || '150', 10);
const duration = __ENV.DURATION || '90s';
const preAllocatedVUs = parseInt(__ENV.PRE_ALLOCATED_VUS || '60', 10);
const maxVUs = parseInt(__ENV.MAX_VUS || '200', 10);

export const options = {
  // p50/p95/p99 explicites : le resume par defaut de k6 n'inclut pas p99, or c'est la colonne
  // que benchmark/normalize compare directement a p99Milliseconds de Sirocco.
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(50)', 'p(95)', 'p(99)'],
  scenarios: {
    checkout: {
      executor: 'ramping-arrival-rate',
      startRate: startRate,
      timeUnit: '1s',
      preAllocatedVUs: preAllocatedVUs,
      maxVUs: maxVUs,
      stages: [
        { target: targetRate, duration: duration },
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
