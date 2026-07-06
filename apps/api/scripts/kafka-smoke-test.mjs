#!/usr/bin/env node
/**
 * Smoke test: Azure Event Hubs (Kafka) or skip if not configured.
 * Usage: npm run kafka:smoke --workspace=apps/api
 */
import { config as loadEnv } from 'dotenv';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';
import { Kafka } from 'kafkajs';

const __dirname = dirname(fileURLToPath(import.meta.url));
loadEnv({ path: resolve(__dirname, '../../../.env') });

const brokers = process.env.KAFKA_BROKERS;
const producerPassword = process.env.KAFKA_PRODUCER_CONNECTION_STRING;
const consumerPassword =
  process.env.KAFKA_CONSUMER_CONNECTION_STRING ?? producerPassword;
const topic = process.env.KAFKA_TOPIC_JOBS ?? 'dual-verify-jobs';

if (!brokers || !producerPassword) {
  console.log('SKIP: KAFKA_BROKERS and producer connection string not set.');
  process.exit(0);
}

function makeKafka(clientId, password) {
  return new Kafka({
    clientId,
    brokers: brokers.split(',').map((b) => b.trim()),
    ssl: true,
    sasl: {
      mechanism: 'plain',
      username: '$ConnectionString',
      password,
    },
  });
}

const producerKafka = makeKafka('bcp-kafka-smoke-producer', producerPassword);
const consumerKafka = makeKafka('bcp-kafka-smoke-consumer', consumerPassword);
const producer = producerKafka.producer();
const consumer = consumerKafka.consumer({ groupId: `smoke-test-${Date.now()}` });

const testMessage = {
  schemaVersion: '1.0',
  smokeTest: true,
  at: new Date().toISOString(),
};

try {
  await consumer.connect();
  await consumer.subscribe({ topic, fromBeginning: false });

  let received = false;
  void consumer.run({
    eachMessage: async ({ message }) => {
      if (message.value?.toString().includes('smokeTest')) {
        received = true;
        console.log('OK: Received smoke test message');
      }
    },
  });

  await new Promise((r) => setTimeout(r, 3000));

  await producer.connect();
  await producer.send({
    topic,
    messages: [{ value: JSON.stringify(testMessage) }],
  });
  console.log(`OK: Sent smoke test message to ${topic}`);

  await new Promise((r) => setTimeout(r, 8000));

  if (!received) {
    console.warn('WARN: Sent OK but consume failed — check bcp-worker-listen policy');
  } else {
    console.log('OK: Kafka producer + consumer working');
  }

  await consumer.disconnect();
  await producer.disconnect();
  console.log('Kafka smoke test finished.');
} catch (err) {
  console.error('FAIL:', err);
  process.exit(1);
}
