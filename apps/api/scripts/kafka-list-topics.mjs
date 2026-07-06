#!/usr/bin/env node
import { config as loadEnv } from 'dotenv';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';
import { Kafka } from 'kafkajs';

const __dirname = dirname(fileURLToPath(import.meta.url));
loadEnv({ path: resolve(__dirname, '../../../.env') });

const brokers = process.env.KAFKA_BROKERS;
const tests = [
  ['listen', process.env.KAFKA_CONSUMER_CONNECTION_STRING],
  ['send', process.env.KAFKA_PRODUCER_CONNECTION_STRING],
  ['worker-send', process.env.KAFKA_WORKER_SEND_CONNECTION_STRING],
];

function makeKafka(password) {
  return new Kafka({
    clientId: 'bcp-list-topics-test',
    brokers: brokers.split(',').map((b) => b.trim()),
    ssl: true,
    sasl: {
      mechanism: 'plain',
      username: '$ConnectionString',
      password,
    },
    connectionTimeout: 30000,
    requestTimeout: 60000,
  });
}

for (const [label, password] of tests) {
  if (!password) {
    console.log(`${label}: SKIP (not set)`);
    continue;
  }
  const admin = makeKafka(password).admin();
  try {
    await admin.connect();
    const topics = await admin.listTopics();
    console.log(`${label}: OK — ${topics.length} topics`);
    console.log('  dual-verify:', topics.filter((t) => t.includes('dual-verify')).join(', ') || '(none)');
    await admin.disconnect();
  } catch (err) {
    console.log(`${label}: FAIL — ${err.message}`);
    try {
      await admin.disconnect();
    } catch {
      /* ignore */
    }
  }
}
