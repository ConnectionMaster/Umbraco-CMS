﻿using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.ModelBinding;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Web.Models.ContentEditing;
using Umbraco.Web.Models.Mapping;

namespace Umbraco.Web.WebApi
{
    /// <summary>
    /// Binds the content model to the controller action for the posted multi-part Post
    /// </summary>
    internal class ContentItemBinder : IModelBinder
    {
        private readonly ApplicationContext _applicationContext;
        private readonly ContentModelMapper _contentModelMapper;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="applicationContext"></param>
        /// <param name="contentModelMapper"></param>
        internal ContentItemBinder(ApplicationContext applicationContext, ContentModelMapper contentModelMapper)
        {
            _applicationContext = applicationContext;
            _contentModelMapper = contentModelMapper;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public ContentItemBinder()
            : this(ApplicationContext.Current, new ContentModelMapper(ApplicationContext.Current))
        {            
        }

        public bool BindModel(HttpActionContext actionContext, ModelBindingContext bindingContext)
        {
            //NOTE: Validation is done in the filter
            if (!actionContext.Request.Content.IsMimeMultipartContent())
            {
                throw new HttpResponseException(HttpStatusCode.UnsupportedMediaType);
            }

            var root = HttpContext.Current.Server.MapPath("~/App_Data/TEMP/FileUploads");
            //ensure it exists
            Directory.CreateDirectory(root);
            var provider = new MultipartFormDataStreamProvider(root);

            var task = Task.Run(() => GetModel(actionContext.Request.Content, provider))
                .ContinueWith(x =>
                {
                    if (x.IsFaulted && x.Exception != null)
                    {
                        throw x.Exception;
                    }
                    bindingContext.Model = x.Result;
                });

            task.Wait();

            return bindingContext.Model != null;
        }

        /// <summary>
        /// Builds the model from the request contents
        /// </summary>
        /// <param name="content"></param>
        /// <param name="provider"></param>
        /// <returns></returns>
        private async Task<ContentItemSave> GetModel(HttpContent content, MultipartFormDataStreamProvider provider)
        {
            var result = await content.ReadAsMultipartAsync(provider);

            if (result.FormData["contentItem"] == null)
            {
                throw new HttpResponseException(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "The request was not formatted correctly and is missing the 'contentItem' parameter"
                    });
            }

            //get the string json from the request
            var contentItem = result.FormData["contentItem"];

            //transform the json into an object
            var model = JsonConvert.DeserializeObject<ContentItemSave>(contentItem);

            //get the files
            foreach (var file in result.FileData)
            {
                var parts = file.Headers.ContentDisposition.Name.Trim(new char[] { '\"' }).Split('_');
                if (parts.Length != 2)
                {
                    throw new HttpResponseException(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "The request was not formatted correctly the file name's must be underscore delimited"
                    });
                }
                int propertyId;
                if (!int.TryParse(parts[1], out propertyId))
                {
                    throw new HttpResponseException(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "The request was not formatted correctly the file name's 2nd part must be an integer"
                    });
                }
                model.UploadedFiles.Add(new ContentItemFile
                    {
                        FilePath = file.LocalFileName,
                        PropertyId = propertyId
                    });
            }

            //finally, let's lookup the real content item and create the DTO item
            model.PersistedContent = _applicationContext.Services.ContentService.GetById(model.Id);
            model.ContentDto = _contentModelMapper.ToContentItemDto(model.PersistedContent);
            //we will now assign all of the values in the 'save' model to the DTO object
            foreach (var p in model.Properties)
            {
                model.ContentDto.Properties.Single(x => x.Id == p.Id).Value = p.Value;
            }

            return model;
        }
    }
}