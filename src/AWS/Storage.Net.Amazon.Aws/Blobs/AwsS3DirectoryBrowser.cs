﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Storage.Net.Blobs;

namespace Storage.Net.Amazon.Aws.Blobs
{
   class AwsS3DirectoryBrowser
   {
      private readonly AmazonS3Client _client;
      private readonly string _bucketName;

      public AwsS3DirectoryBrowser(AmazonS3Client client, string bucketName)
      {
         _client = client;
         _bucketName = bucketName;
      }

      public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions options, CancellationToken cancellationToken)
      {
         var container = new List<Blob>();

         await ListFolderAsync(container, options.FolderPath, options, cancellationToken).ConfigureAwait(false);

         return options.MaxResults == null
            ? container
            : container.Count > options.MaxResults.Value
               ? container.Take(options.MaxResults.Value).ToList()
               : container;
      }

      private async Task ListFolderAsync(List<Blob> container, string path, ListOptions options, CancellationToken cancellationToken)
      {
         var request = new ListObjectsV2Request()
         {
            BucketName = _bucketName,
            Prefix = FormatFolderPrefix(path),
            Delimiter = "/"   //this tells S3 not to go into the folder recursively
         };

         var folderContainer = new List<Blob>();

         while(options.MaxResults == null || (container.Count < options.MaxResults))
         {
            ListObjectsV2Response response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);

            folderContainer.AddRange(ToBlobs(path, response, options));

            if(response.NextContinuationToken == null)
               break;

            request.ContinuationToken = response.NextContinuationToken;
         }

         container.AddRange(folderContainer);

         if(options.Recurse)
         {
            List<Blob> folders = folderContainer.Where(b => b.Kind == BlobItemKind.Folder).ToList();

            await Task.WhenAll(folders.Select(f => ListFolderAsync(container, f.FullPath, options, cancellationToken))).ConfigureAwait(false);
         }
      }

      private static IReadOnlyCollection<Blob> ToBlobs(string path, ListObjectsV2Response response, ListOptions options)
      {
         var result = new List<Blob>();

         //the files are listed as the S3Objects member, but they don't specifically contain folders,
         //but even if they do, they need to be filtered out

         result.AddRange(
            response.S3Objects
               .Where(b => !b.Key.EndsWith("/")) //check if this is "virtual folder" as S3 console creates them (rubbish)
               .Select(ToBlob)
               .Where(options.IsMatch)
               .Where(b => options.BrowseFilter == null || options.BrowseFilter(b)));

         //subfolders are listed in another field (what a funny name!)

         //prefix is absolute too
         result.AddRange(
            response.CommonPrefixes
               .Select(p => new Blob(p, BlobItemKind.Folder)));

         return result;
      }

      private static string FormatFolderPrefix(string folderPath)
      {
         folderPath = StoragePath.Normalize(folderPath);

         if(StoragePath.IsRootPath(folderPath))
            return null;

         if(!folderPath.EndsWith("/"))
            folderPath += "/";

         return folderPath;
      }

      private static Blob ToBlob(S3Object s3Obj)
      {
         if(s3Obj.Key.EndsWith("/"))
            return new Blob(s3Obj.Key, BlobItemKind.Folder);

         //Key is an absolute path
         return new Blob(s3Obj.Key, BlobItemKind.File) { Size = s3Obj.Size };
      }

      public async Task DeleteRecursiveAsync(string fullPath, CancellationToken cancellationToken)
      {
         var request = new ListObjectsV2Request()
         {
            BucketName = _bucketName,
            Prefix = fullPath + "/"
         };

         while(true)
         {
            ListObjectsV2Response response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);

            await Task.WhenAll(response.S3Objects.Select(s3 => _client.DeleteObjectAsync(_bucketName, s3.Key, cancellationToken)));

            if(response.NextContinuationToken == null)
               break;

            request.ContinuationToken = response.NextContinuationToken;
         }
      }
   }
}