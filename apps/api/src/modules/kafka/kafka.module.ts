import { Module } from '@nestjs/common';
import { KafkaConfigService } from './kafka.config';
import { KafkaProducerService } from './kafka-producer.service';
import { LocalJobQueueService } from './local-job-queue.service';

@Module({
  providers: [KafkaConfigService, KafkaProducerService, LocalJobQueueService],
  exports: [KafkaConfigService, KafkaProducerService, LocalJobQueueService],
})
export class KafkaModule {}
