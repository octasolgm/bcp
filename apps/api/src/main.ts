import { config as loadEnv } from 'dotenv';
import { join } from 'path';
import { resolve } from 'path';
import WebSocket from 'ws';
import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import { NestExpressApplication } from '@nestjs/platform-express';
import { DocumentBuilder, SwaggerModule } from '@nestjs/swagger';
import { AppModule } from './app.module';

loadEnv({ path: resolve(__dirname, '../.env') });
loadEnv({ path: resolve(__dirname, '../../../.env'), override: true });

// Supabase realtime requires WebSocket on Node.js < 22
// @ts-expect-error Node ws package used as global WebSocket polyfill
global.WebSocket = WebSocket;

async function bootstrap(): Promise<void> {
  const app = await NestFactory.create<NestExpressApplication>(AppModule);

  app.useStaticAssets(join(__dirname, 'swagger-ui'));

  app.enableCors({
    origin: [
      'http://localhost:3000',
      'http://localhost:3001',
      'http://localhost:3002',
    ],
    credentials: true,
  });

  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
    }),
  );

  const swaggerConfig = new DocumentBuilder()
    .setTitle('BCP API')
    .setDescription('Bank Compliance Platform — upload, extract, analyze, compare')
    .setVersion('0.1.0')
    .build();
  const document = SwaggerModule.createDocument(app, swaggerConfig);
  const swaggerOptions = {
    customJs: '/swagger-ui-custom.js',
    customCssUrl: '/swagger-ui-custom.css',
    swaggerOptions: {
      docExpansion: 'list',
      tagsSorter: 'alpha',
      operationsSorter: 'alpha',
    },
  };
  SwaggerModule.setup('ai/swagger', app, document, swaggerOptions);

  const http = app.getHttpAdapter().getInstance();
  http.get('/swagger', (_req: unknown, res: { redirect: (url: string) => void }) => {
    res.redirect('/ai/swagger/');
  });

  const port = process.env.PORT ? Number(process.env.PORT) : 4000;
  await app.listen(port);
  // eslint-disable-next-line no-console
  console.log(`BCP API listening on http://localhost:${port}`);
}

void bootstrap();
