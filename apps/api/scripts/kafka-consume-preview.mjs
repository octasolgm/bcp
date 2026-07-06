#!/usr/bin/env node
/**
 * Peek at recent messages on dual-verify-jobs (Azure Event Hubs Kafka).
 * Usage: node apps/api/scripts/kafka-consume-preview.mjs [topic] [seconds]
 */
import { config as loadEnv } from 'dotenv';
import { resolve, dirname } from 'path';
import { fileURLToPath } from 'url';
import { Kafka } from 'kafkajs';

const __dirname = dirname(fileURLToPath(import.meta.url));
loadEnv({ path: resolve(__dirname, '../../../.env') });

const topic = process.argv[2] ?? process.env.KAFKA_TOPIC_JOBS ?? 'dual-verify-jobs';
const seconds = Number(process.argv[3] ?? 15);
const password = process.env.KAFKA_CONSUMER_CONNECTION_STRING;
const brokers = process.env.KAFKA_BROKERS;

if (!brokers || !password) {
  console.error('Set KAFKA_BROKERS and KAFKA_CONSUMER_CONNECTION_STRING in .env');
  process.exit(1);
}

const kafka = new Kafka({
  clientId: 'bcp-kafka-preview',
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

const consumer = kafka.consumer({ groupId: `preview-${Date.now()}` });
let count = 0;

console.log(`Listening on "${topic}" for ${seconds}s (Ctrl+C to stop early)...\n`);

await consumer.connect();
await consumer.subscribe({ topic, fromBeginning: false });

await consumer.run({
  eachMessage: async ({ partition, message }) => {
    count += 1;
    const value = message.value?.toString() ?? '';
    let pretty = value;
    try {
      pretty = JSON.stringify(JSON.parse(value), null, 2);
    } catch {
      /* raw */
    }
    console.log(`--- message #${count} partition ${partition} offset ${message.offset} ---`);
    console.log(pretty);
    console.log('');
  },
});

await new Promise((r) => setTimeout(r, seconds * 1000));
await consumer.disconnect();
console.log(`Done. Received ${count} message(s).`);
