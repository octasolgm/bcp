import {
  BadRequestException,
  Body,
  Controller,
  Get,
  Param,
  Patch,
  Post,
  Query,
  Res,
  UploadedFiles,
  UseInterceptors,
} from '@nestjs/common';
import { AnyFilesInterceptor } from '@nestjs/platform-express';
import { ApiConsumes, ApiOperation, ApiTags } from '@nestjs/swagger';
import type { Response } from 'express';
import { BcpwebService } from './bcpweb.service';
import { BcpwebExcelService } from './bcpweb-excel.service';
import type { BcpwebUpdateItemDto } from './bcpweb.types';

@ApiTags('BCP Web')
@Controller('bcpweb')
export class BcpwebController {
  constructor(
    private readonly bcpwebService: BcpwebService,
    private readonly excelService: BcpwebExcelService,
  ) {}

  @Get('health')
  @ApiOperation({ summary: 'BCP Web module health' })
  health() {
    return { success: true, data: { status: 'ok', module: 'bcpweb' } };
  }

  @Get('dashboard')
  getDashboard() {
    return { success: true, data: this.bcpwebService.getDashboard() };
  }

  @Get('branches')
  getBranches() {
    return { success: true, data: this.bcpwebService.getBranches() };
  }

  @Get('regulations')
  getRegulations(@Query('category') category?: string) {
    return { success: true, data: this.bcpwebService.getRegulations(category) };
  }

  @Get('documents')
  getDocuments(@Query('category') category?: string) {
    return { success: true, data: this.bcpwebService.getDocuments(category) };
  }

  @Get('analysis/sessions/demo')
  getDemoSession() {
    return { success: true, data: this.bcpwebService.getDemoSession() };
  }

  @Get('analysis/sessions/:id')
  getSession(@Param('id') id: string) {
    return { success: true, data: this.bcpwebService.getSession(id) };
  }

  @Get('analysis/sessions/:id/progress')
  getProgress(@Param('id') id: string) {
    return { success: true, data: this.bcpwebService.getProgress(id) };
  }

  @Get('analysis/sessions/:id/items')
  getItems(@Param('id') id: string) {
    return { success: true, data: this.bcpwebService.getSessionItems(id) };
  }

  @Post('analysis/sessions')
  createSession(
    @Body()
    body: {
      regulationId: string;
      internalDocName: string;
      regulationDocName: string;
    },
  ) {
    return this.bcpwebService.createSession(body).then((data) => ({
      success: true,
      data,
    }));
  }

  @Post('analysis/sessions/upload')
  @ApiOperation({ summary: 'Create analysis session with PDF uploads (Gemini)' })
  @ApiConsumes('multipart/form-data')
  @UseInterceptors(AnyFilesInterceptor())
  async createSessionUpload(
    @UploadedFiles() files: Express.Multer.File[],
    @Body('regulationId') regulationId: string,
  ) {
    if (!regulationId?.trim()) {
      throw new BadRequestException('regulationId is required');
    }

    const internal = files?.find(
      (f) => f.fieldname === 'internalFile' || f.fieldname === 'internal',
    );
    const regulation = files?.find(
      (f) => f.fieldname === 'regulationFile' || f.fieldname === 'regulation',
    );

    if (!internal?.buffer?.length) {
      throw new BadRequestException('internalFile (PDF/DOCX) is required');
    }
    if (!regulation?.buffer?.length) {
      throw new BadRequestException('regulationFile (PDF) is required');
    }

    const data = await this.bcpwebService.createSessionWithFiles(regulationId, {
      internalBuffer: internal.buffer,
      regulationBuffer: regulation.buffer,
      internalFileName: internal.originalname,
      regulationFileName: regulation.originalname,
    });

    return { success: true, data };
  }

  @Patch('analysis/sessions/:sessionId/items/:itemId')
  updateItem(
    @Param('sessionId') sessionId: string,
    @Param('itemId') itemId: string,
    @Body() dto: BcpwebUpdateItemDto,
  ) {
    return {
      success: true,
      data: this.bcpwebService.updateItem(sessionId, itemId, dto),
    };
  }

  @Post('analysis/sessions/:sessionId/items/:itemId/sign-off')
  signOff(
    @Param('sessionId') sessionId: string,
    @Param('itemId') itemId: string,
  ) {
    return {
      success: true,
      data: this.bcpwebService.signOffItem(sessionId, itemId),
    };
  }

  @Get('pdf/page')
  getPdfPage(
    @Query('sessionId') sessionId: string,
    @Query('source') source: 'regulation' | 'policy',
    @Query('page') page: string,
    @Query('itemId') itemId?: string,
  ) {
    return {
      success: true,
      data: this.bcpwebService.getPdfPage(
        sessionId,
        source ?? 'regulation',
        Number(page) || 1,
        itemId,
      ),
    };
  }

  @Get('excel/export/:sessionId')
  @ApiOperation({ summary: 'Download Gap_Analysis_Working.xlsx' })
  async exportExcel(
    @Param('sessionId') sessionId: string,
    @Res() res: Response,
  ): Promise<void> {
    const buffer = await this.excelService.buildExport(sessionId);
    res.setHeader(
      'Content-Type',
      'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    );
    res.setHeader(
      'Content-Disposition',
      'attachment; filename="Gap_Analysis_Working.xlsx"',
    );
    res.send(buffer);
  }
}
