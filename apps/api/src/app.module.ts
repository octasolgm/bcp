import { Module } from '@nestjs/common';
import { ConfigModule } from '@nestjs/config';
import { ScheduleModule } from '@nestjs/schedule';
import { resolve } from 'path';
import { SupabaseModule } from './common/supabase/supabase.module';
import { DatabaseModule } from './common/database/database.module';
import { GeminiModule } from './common/gemini/gemini.module';
import { HealthModule } from './modules/health/health.module';
import { DocumentsModule } from './modules/documents/documents.module';
import { ExtractionModule } from './modules/extraction/extraction.module';
import { RequirementsModule } from './modules/requirements/requirements.module';
import { RagModule } from './modules/rag/rag.module';
import { ComparisonModule } from './modules/comparison/comparison.module';
import { ExcelModule } from './modules/excel/excel.module';
import { DashboardModule } from './modules/dashboard/dashboard.module';
import { ComplianceItemsModule } from './modules/compliance-items/compliance-items.module';
import { AlertsModule } from './modules/alerts/alerts.module';
import { AiModule } from './modules/ai/ai.module';
import { LandingAiModule } from './modules/landing-ai/landing-ai.module';
import { BcpwebModule } from './modules/bcpweb/bcpweb.module';
import { DualVerifyKafkaModule } from './modules/dual-verify-kafka/dual-verify-kafka.module';

@Module({
  imports: [
    ConfigModule.forRoot({
      isGlobal: true,
      envFilePath: [
        resolve(__dirname, '../.env'),
        resolve(__dirname, '../../../.env'),
      ],
    }),
    ScheduleModule.forRoot(),
    DatabaseModule,
    SupabaseModule,
    GeminiModule,
    HealthModule,
    DocumentsModule,
    ExtractionModule,
    RequirementsModule,
    RagModule,
    ComparisonModule,
    ExcelModule,
    DashboardModule,
    ComplianceItemsModule,
    AlertsModule,
    AiModule,
    LandingAiModule,
    BcpwebModule,
    DualVerifyKafkaModule,
  ],
})
export class AppModule {}
