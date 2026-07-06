import { Module } from '@nestjs/common';
import { AiModule } from '../ai/ai.module';
import { LandingAiModule } from '../landing-ai/landing-ai.module';
import { BcpwebAnalysisService } from './bcpweb-analysis.service';
import { BcpwebController } from './bcpweb.controller';
import { BcpwebService } from './bcpweb.service';
import { BcpwebExcelService } from './bcpweb-excel.service';

/** BCP Web (Reguliq UI) — Gemini + Landing AI gap analysis */
@Module({
  imports: [AiModule, LandingAiModule],
  controllers: [BcpwebController],
  providers: [BcpwebService, BcpwebAnalysisService, BcpwebExcelService],
  exports: [BcpwebService],
})
export class BcpwebModule {}
