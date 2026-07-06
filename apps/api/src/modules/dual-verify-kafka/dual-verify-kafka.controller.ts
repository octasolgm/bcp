import {
  Body,
  Controller,
  Get,
  Param,
  Post,
  UploadedFile,
  UseInterceptors,
  BadRequestException,
} from '@nestjs/common';
import { FileInterceptor } from '@nestjs/platform-express';
import { ApiBody, ApiConsumes, ApiOperation, ApiTags } from '@nestjs/swagger';
import { DualVerifyKafkaService } from './dual-verify-kafka.service';
import type { CreateDualVerifyJobDto } from './dual-verify-kafka.types';

@ApiTags('dual-verify-kafka')
@Controller('dual-verify-kafka')
export class DualVerifyKafkaController {
  constructor(private readonly service: DualVerifyKafkaService) {}

  @Get('health')
  @ApiOperation({ summary: 'Kafka dual verify module health + transport mode' })
  async health() {
    return { success: true, data: await this.service.getHealth() };
  }

  @Get('sessions')
  @ApiOperation({ summary: 'List recent Kafka dual verify sessions' })
  async listSessions() {
    const sessions = await this.service.listSessions(30);
    return {
      success: true,
      data: sessions.map((s) => ({
        id: s.id,
        status: s.status,
        granularity: s.granularity,
        totalPoints: s.totalPoints,
        completedPoints: s.completedPoints,
        failedPoints: s.failedPoints,
        phase2Model: s.phase2Model,
        transport: s.transport,
        updatedAt: s.updatedAt,
        label: `${s.granularity} · ${s.completedPoints}/${s.totalPoints} done · ${s.updatedAt.slice(0, 16)}`,
      })),
    };
  }

  @Post('jobs/json')
  @ApiOperation({ summary: 'Start dual verify session (JSON body)' })
  async createJobJson(@Body() dto: CreateDualVerifyJobDto) {
    const session = await this.service.createJob(dto);
    return { success: true, data: session };
  }

  @Post('jobs')
  @ApiOperation({
    summary: 'Start Kafka dual verify session — one queue message per gov point',
  })
  @ApiConsumes('multipart/form-data', 'application/json')
  @ApiBody({
    schema: {
      type: 'object',
      properties: {
        pointIds: {
          type: 'array',
          items: { type: 'string' },
          description: 'Gov point IDs to analyze',
        },
        granularity: { type: 'string', enum: ['section', 'leaf'] },
        govDocId: { type: 'string', default: 'gov-tfs-guidelines' },
        internalDocId: { type: 'string', default: 'internal-imptfs' },
        phase2Model: { type: 'string' },
        forceRefresh: {
          type: 'boolean',
          description: 'When true, bypass Phase 1 Supabase cache (uses Landing AI credits)',
        },
        internalFile: { type: 'string', format: 'binary' },
      },
    },
  })
  @UseInterceptors(FileInterceptor('internalFile'))
  async createJob(
    @Body() body: Record<string, string | string[]>,
    @UploadedFile() internalFile?: Express.Multer.File,
  ) {
    const dto = this.parseCreateDto(body);
    const session = await this.service.createJob(
      dto,
      internalFile?.buffer,
    );
    return { success: true, data: session };
  }

  @Get('jobs/:sessionId')
  @ApiOperation({ summary: 'Poll session progress (completed / failed / running counts)' })
  async getProgress(@Param('sessionId') sessionId: string) {
    const data = await this.service.getProgress(sessionId);
    return { success: true, data };
  }

  @Get('jobs/:sessionId/results')
  @ApiOperation({ summary: 'Get per-point dual verify results' })
  async getResults(@Param('sessionId') sessionId: string) {
    const data = await this.service.getResults(sessionId);
    return { success: true, data };
  }

  @Post('jobs/:sessionId/retry-failed')
  @ApiOperation({ summary: 'Re-queue all failed points in a session' })
  async retryFailed(@Param('sessionId') sessionId: string) {
    const data = await this.service.retryFailed(sessionId);
    return { success: true, data };
  }

  private parseCreateDto(body: Record<string, string | string[]>): CreateDualVerifyJobDto {
    let pointIds: string[] = [];

    if (typeof body.pointIds === 'string') {
      try {
        const parsed = JSON.parse(body.pointIds) as unknown;
        if (Array.isArray(parsed)) {
          pointIds = parsed.map(String);
        } else {
          pointIds = body.pointIds.split(',').map((s) => s.trim()).filter(Boolean);
        }
      } catch {
        pointIds = body.pointIds.split(',').map((s) => s.trim()).filter(Boolean);
      }
    } else if (Array.isArray(body.pointIds)) {
      pointIds = body.pointIds.map(String);
    }

    if (!pointIds.length) {
      throw new BadRequestException('pointIds is required (JSON array or comma-separated)');
    }

    return {
      pointIds,
      granularity:
        body.granularity === 'leaf' ? 'leaf' : 'section',
      govDocId: typeof body.govDocId === 'string' ? body.govDocId : undefined,
      internalDocId:
        typeof body.internalDocId === 'string' ? body.internalDocId : undefined,
      phase2Model:
        typeof body.phase2Model === 'string' ? body.phase2Model : undefined,
      forceRefresh: this.parseBooleanField(body.forceRefresh),
    };
  }

  /** Parse multipart form booleans (`true`, `1`, or array first element). */
  private parseBooleanField(value: string | string[] | undefined): boolean {
    const raw = Array.isArray(value) ? value[0] : value;
    return raw === 'true' || raw === '1';
  }
}
