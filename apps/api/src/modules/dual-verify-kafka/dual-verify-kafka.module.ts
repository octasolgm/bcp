import { Module } from '@nestjs/common';
import { AiModule } from '../ai/ai.module';
import { KafkaModule } from '../kafka/kafka.module';
import { LandingAiModule } from '../landing-ai/landing-ai.module';
import { DualVerifyKafkaController } from './dual-verify-kafka.controller';
import { DualVerifyKafkaService } from './dual-verify-kafka.service';
import { DualVerifyKafkaStoreService } from './dual-verify-kafka-store.service';
import { DualVerifyKafkaWorkerService } from './dual-verify-kafka-worker.service';

@Module({
  imports: [KafkaModule, LandingAiModule, AiModule],
  controllers: [DualVerifyKafkaController],
  providers: [
    DualVerifyKafkaStoreService,
    DualVerifyKafkaService,
    DualVerifyKafkaWorkerService,
  ],
  exports: [DualVerifyKafkaService],
})
export class DualVerifyKafkaModule {}
